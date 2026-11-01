package com.phonedrive.android.pairing

import android.content.Context
import android.os.Build
import com.phonedrive.android.remote.ClientKey
import com.phonedrive.android.remote.DeviceDirectory
import com.phonedrive.android.remote.PairedDevice
import com.phonedrive.android.security.DeviceIdentity
import com.phonedrive.android.server.AuthorizedKeysStore
import com.phonedrive.android.server.SftpServerService
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.security.interfaces.RSAPublicKey

/** 配对失败原因，message 可直接展示给用户（不含任何密钥/配对码） */
class PairingException(message: String) : Exception(message)

/**
 * 配对加入方 B：解析二维码 → GET /info → POST /pair → 校验对端 Host Key 指纹
 * → 把对端 clientPub 写入本机 authorized_keys → 保存配对记录。
 *
 * 也支持手动输入 IP + 端口 + 配对码（扫码不可用时），流程与协议 §3 完全相同。
 */
object PairingClient {

    private const val CONNECT_TIMEOUT_MS = 6_000
    private const val READ_TIMEOUT_MS = 70_000
    private val IPV4_PATTERN = Regex("^\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}$")

    private data class PeerInfo(
        val version: Int,
        val deviceId: String,
        val name: String,
        val type: String,
        val fingerprint: String,
        val sftpPort: Int,
        val nonce: String,
    )

    private data class PairAck(
        val deviceId: String,
        val name: String,
        val type: String,
        val hostFp: String,
        val hostPub: String,
        val clientPub: String,
        val nonce: String,
    )

    /** 扫码配对：二维码内容为协议 §2 的 JSON */
    suspend fun joinByQr(context: Context, rawQr: String): PairedDevice = withContext(Dispatchers.IO) {
        val payload = PairingPayload.parse(rawQr)
            ?: throw PairingException("二维码内容不是有效的 PhoneDrive 配对信息")
        if (!isValidIpv4(payload.ip)) throw PairingException("二维码中的 IP 地址格式非法")
        pairInternal(context, payload.ip, payload.port, payload.code, payload.fingerprint, null)
    }

    /** 手动配对：IP + 端口(默认 47821) + 6 位配对码 */
    suspend fun joinManually(
        context: Context,
        ip: String,
        port: Int,
        code: String,
        fingerprint: String,
    ): PairedDevice = withContext(Dispatchers.IO) {
        val address = ip.trim()
        if (!isValidIpv4(address)) throw PairingException("请输入正确的 IPv4 地址")
        if (port !in 1..65535) throw PairingException("端口必须在 1-65535 之间")
        if (!code.trim().matches(Regex("^[0-9]{6}$"))) throw PairingException("配对码必须是 6 位数字")
        pairInternal(context, address, port, code.trim(), fingerprint.trim(), null)
    }

    private fun pairInternal(
        context: Context,
        ip: String,
        port: Int,
        code: String,
        expectedFingerprint: String?,
        knownInfo: PeerInfo?,
    ): PairedDevice {
        val pinned = expectedFingerprint ?: throw PairingException("请扫描二维码或提供已核对的完整 Host Key 指纹")
        val info = knownInfo ?: fetchInfo(ip, port, pinned)
        if (info.version != 2) throw PairingException("对方协议版本不受支持（${info.version}）")
        if (info.deviceId.isBlank()) throw PairingException("对方未返回设备 ID，已中止配对")
        if (!info.fingerprint.startsWith("SHA256:")) {
            throw PairingException("对方未提供 Host Key 指纹，无法验证身份")
        }
        if (expectedFingerprint != null && info.fingerprint != expectedFingerprint) {
            throw PairingException("身份不匹配：配对服务的 Host Key 指纹与二维码不一致，已中止配对")
        }
        val expected = expectedFingerprint ?: info.fingerprint

        val hostKey = DeviceIdentity.getOrCreateHostKeyPair()
        val hostPub = hostKey.public as RSAPublicKey
        val selfDeviceId = DeviceIdentity.getDeviceId(context)
        val selfHostFp = DeviceIdentity.fingerprintSha256(hostPub)
        val selfHostPub = DeviceIdentity.toOpenSshPublicKey(hostPub)
        val selfClientPub = ClientKey.openSshPublicKeyLine()

        val transcript = PairingProof.transcript(info.nonce, selfDeviceId, Build.MODEL, "phone", selfHostFp, selfHostPub, selfClientPub)
        val request = JSONObject().apply {
            put("clientProof", PairingProof.sign(ClientKey.getOrCreateKeyPair().private, transcript))
            put("hostProof", PairingProof.sign(hostKey.private, transcript))
            put("v", 2)
            put("nonce", info.nonce)
            put("code", code)
            put("deviceid", selfDeviceId)
            put("name", Build.MODEL)
            put("type", "phone")
            put("hostFp", selfHostFp)
            put("hostPub", selfHostPub)
            put("clientPub", selfClientPub)
        }.toString()

        val (status, text) = request(ip, port, "POST", "/pair", request, pinned)
        val ack = parseAck(status, text)
        if (ack.nonce != info.nonce || ack.deviceId != info.deviceId || ack.hostFp != expected) {
            throw PairingException("身份不匹配：对方返回的 Host Key 指纹与预期不一致，已中止配对")
        }
        if (!ack.hostPub.startsWith("ssh-") || !ack.clientPub.startsWith("ssh-")) {
            throw PairingException("对方返回的公钥格式非法，已中止配对")
        }

        if (DeviceIdentity.fingerprintSha256(PairingProof.rsa(ack.hostPub)) != expected)
            throw PairingException("返回公钥与 TLS/二维码身份不一致")
        PairingProof.rsa(ack.clientPub)
        val clientBlob = ack.clientPub.trim().split(Regex("\\s+"))[1]
        AuthorizedKeysStore(context).add(ack.clientPub, ack.name.take(64))

        val device = PairedDevice(
            deviceId = ack.deviceId,
            name = ack.name,
            type = ack.type,
            hostKeyFingerprint = ack.hostFp,
            lastKnownIp = ip,
            lastKnownPort = if (ack.type == "windows") 0 else info.sftpPort,
            lastSeenAt = System.currentTimeMillis(),
            peerKeyBlob = clientBlob,
            peerHostPub = ack.hostPub,
        )
        DeviceDirectory.getInstance(context).add(device)
        return device
    }

    private fun fetchInfo(ip: String, port: Int, expected: String): PeerInfo {
        val (status, text) = request(ip, port, "GET", "/info", null, expected)
        if (status != 200) {
            throw PairingException("对方配对服务不可达（HTTP $status），请确认对方仍停留在配对页面")
        }
        val obj = runCatching { JSONObject(text) }.getOrNull()
            ?: throw PairingException("对方返回的配对信息格式非法")
        return PeerInfo(
            version = obj.optInt("v", 0),
            nonce = obj.optString("nonce").also { require(java.util.Base64.getDecoder().decode(it).size == 32) },
            deviceId = obj.optString("deviceid").trim(),
            name = obj.optString("name").trim().ifBlank { "未命名设备" },
            type = obj.optString("type").trim().ifBlank { "phone" },
            fingerprint = obj.optString("fp").trim(),
            sftpPort = obj.optInt("sftpport", SftpServerService.DEFAULT_PORT)
                .takeIf { it in 1..65535 } ?: SftpServerService.DEFAULT_PORT,
        )
    }

    private fun parseAck(status: Int, text: String): PairAck {
        val obj = runCatching { JSONObject(text) }.getOrNull()
        if (status != 200 || obj == null || !obj.optBoolean("ok", false)) {
            val error = obj?.optString("error").orEmpty()
            val message = when {
                status == 401 || error == "bad_code" -> "配对码错误，请核对后重试"
                status == 409 || error == "code_expired" -> "对方的配对窗口已过期或错误次数超限，请重新生成二维码"
                status == 400 || error == "bad_request" -> "对方拒绝了配对请求（请求字段缺失或格式非法）"
                else -> "配对失败（HTTP $status）"
            }
            throw PairingException(message)
        }
        val deviceId = obj.optString("deviceid").trim()
        val hostFp = obj.optString("hostFp").trim()
        val hostPub = obj.optString("hostPub").trim()
        val clientPub = obj.optString("clientPub").trim()
        if (deviceId.isEmpty() || hostFp.isEmpty() || hostPub.isEmpty() || clientPub.isEmpty()) {
            throw PairingException("对方返回的配对结果缺少必要字段，已中止配对")
        }
        return PairAck(
            deviceId = deviceId,
            nonce = obj.optString("nonce"),
            name = obj.optString("name").trim().ifBlank { "未命名设备" },
            type = obj.optString("type").trim().ifBlank { "phone" },
            hostFp = hostFp,
            hostPub = hostPub,
            clientPub = clientPub,
        )
    }

    private fun request(
        ip: String,
        port: Int,
        method: String,
        path: String,
        body: String?,
        expected: String,
    ): Pair<Int, String> {
        val connection = try {
            (URL("https://$ip:$port$path").openConnection(java.net.Proxy.NO_PROXY) as javax.net.ssl.HttpsURLConnection).apply {
                sslSocketFactory = PairingTls.clientContext(expected).socketFactory
                hostnameVerifier = PairingTls.verifier(ip, expected)
                instanceFollowRedirects = false
                requestMethod = method
                connectTimeout = CONNECT_TIMEOUT_MS
                readTimeout = READ_TIMEOUT_MS
                setRequestProperty("Accept", "application/json")
                useCaches = false
            }
        } catch (e: IOException) {
            throw PairingException("无法连接到 $ip:$port：${e.message ?: "网络错误"}")
        }
        try {
            if (body != null) {
                connection.doOutput = true
                connection.setRequestProperty("Content-Type", "application/json; charset=utf-8")
                connection.outputStream.use { it.write(body.toByteArray(Charsets.UTF_8)) }
            }
            val status = connection.responseCode
            val stream = if (status in 200..299) connection.inputStream else connection.errorStream
            val text = stream?.use { input ->
                val result = java.io.ByteArrayOutputStream()
                val buffer = ByteArray(4096)
                while (true) {
                    val n = input.read(buffer); if (n < 0) break
                    if (result.size() + n > 65536) throw IOException("配对响应过大")
                    result.write(buffer, 0, n)
                }
                result.toString("UTF-8")
            }.orEmpty()
            return status to text
        } catch (e: IOException) {
            throw PairingException("与 $ip:$port 通信失败：${e.message ?: "网络错误"}")
        } finally {
            runCatching { connection.disconnect() }
        }
    }

    private fun isValidIpv4(ip: String): Boolean {
        if (!IPV4_PATTERN.matches(ip)) return false
        return ip.split('.').all { it.toIntOrNull()?.let { value -> value in 0..255 } == true }
    }
}
