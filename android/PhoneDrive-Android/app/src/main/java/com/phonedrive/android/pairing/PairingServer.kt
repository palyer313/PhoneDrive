package com.phonedrive.android.pairing

import android.content.Context
import android.os.Build
import com.phonedrive.android.remote.ClientKey
import com.phonedrive.android.remote.DeviceDirectory
import com.phonedrive.android.remote.PairedDevice
import com.phonedrive.android.security.DeviceIdentity
import com.phonedrive.android.server.AuthorizedKeysStore
import com.phonedrive.android.server.SftpServerService
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import org.json.JSONObject
import java.io.BufferedInputStream
import java.io.Closeable
import java.io.IOException
import java.io.InputStream
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketTimeoutException
import java.security.interfaces.RSAPublicKey

/**
 * 配对 HTTP 服务（协议 pairing/README.md §3）。
 *
 * - 仅绑定本机局域网 IPv4 地址，端口 47821，窗口期 ≤120 秒；
 * - 手写 HTTP/1.1，不引入 Ktor/OkHttp；
 * - 实现 GET /info 与 POST /pair，成功时把对端 clientPub 写入 authorized_keys，
 *   并保存对端 deviceId/name/type/hostFp/hostPub/lastKnownIp；
 * - 配对成功、超时或取消后自动关闭监听 socket。
 *
 * 私钥、配对码、公钥均不写入日志。
 */
class PairingServer(
    private val context: Context,
    private val session: PairingSession,
) : Closeable {

    companion object {
        const val PORT = 47821
        private const val MAX_BODY_BYTES = 64 * 1024
        private const val MAX_LINE_BYTES = 8 * 1024
        private const val SOCKET_TIMEOUT_MS = 5_000
        private const val BACKLOG = 8
        private const val MAX_FIELD_LENGTH = 512
        private val CODE_PATTERN = Regex("^[0-9]{6}$")
        private val ALLOWED_TYPES = setOf("phone", "tablet", "windows")
    }

    data class Status(
        val running: Boolean = false,
        val ip: String? = null,
        val port: Int = PORT,
        val error: String? = null,
    )

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val _status = MutableStateFlow(Status())
    val status: StateFlow<Status> = _status.asStateFlow()

    private var serverSocket: ServerSocket? = null
    private var acceptJob: Job? = null

    /** 启动监听；绑定失败时通过 status.error 暴露原因，不抛异常给 UI */
    @Synchronized
    fun start() {
        if (serverSocket != null) return
        val ip = LanAddress.preferredIpv4()
        if (ip == null) {
            _status.value = Status(error = "未检测到局域网 IPv4 地址，请先连接 Wi-Fi 后重试")
            return
        }
        val socket = try {
            (PairingTls.serverContext().serverSocketFactory.createServerSocket() as javax.net.ssl.SSLServerSocket).apply {
                enabledProtocols = arrayOf("TLSv1.2") // Keystore RSA identity permits PKCS#1; TLS 1.3 RSA requires PSS.
                reuseAddress = true
                bind(InetSocketAddress(ip, PORT), BACKLOG)
                soTimeout = 1_000
            }
        } catch (e: Exception) {
            _status.value = Status(
                error = "无法在 $ip:$PORT 启动配对服务：${e.message ?: e.javaClass.simpleName}"
            )
            return
        }
        serverSocket = socket
        _status.value = Status(running = true, ip = ip, port = PORT)
        session.start()
        acceptJob = scope.launch { acceptLoop(socket) }
    }

    override fun close() {
        synchronized(this) {
            acceptJob?.cancel()
            acceptJob = null
            val socket = serverSocket
            serverSocket = null
            runCatching { socket?.close() }
        }
        session.cancel()
        session.close()
        scope.cancel()
        _status.value = _status.value.copy(running = false)
    }

    // ------------------------------------------------------------ 监听循环

    private suspend fun acceptLoop(socket: ServerSocket) {
        while (currentCoroutineContext().isActive) {
            if (session.state.value.state != PairingSession.State.PENDING) break
            val client = try {
                socket.accept()
            } catch (timeout: SocketTimeoutException) {
                continue
            } catch (e: IOException) {
                break
            }
            try {
                handle(client)
            } catch (e: Exception) {
                // 单个请求解析异常不影响配对窗口继续等待
            } finally {
                runCatching { client.close() }
            }
            if (session.state.value.state == PairingSession.State.SUCCESS) break
        }
        runCatching { socket.close() }
        _status.value = _status.value.copy(running = false)
    }

    // ------------------------------------------------------------ HTTP 解析

    private fun handle(client: Socket) {
        client.soTimeout = SOCKET_TIMEOUT_MS
        val input = BufferedInputStream(client.getInputStream())
        val requestLine = readLine(input) ?: return
        val parts = requestLine.split(' ')
        if (parts.size < 2) {
            writeResponse(client, reply(400, errorBody("bad_request")))
            return
        }
        val method = parts[0].uppercase()
        val path = parts[1].substringBefore('?')

        var contentLength = 0
        while (true) {
            val line = readLine(input) ?: break
            if (line.isEmpty()) break
            if (line.substringBefore(':').trim().equals("Content-Length", ignoreCase = true)) {
                contentLength = line.substringAfter(':').trim().toIntOrNull() ?: 0
            }
        }

        if (contentLength > MAX_BODY_BYTES) {
            writeResponse(client, reply(400, errorBody("bad_request")))
            return
        }
        val body = if (contentLength > 0) readBody(input, contentLength) else ""

        val response = when {
            method == "GET" && path == "/info" -> reply(200, infoBody())
            method == "POST" && path == "/pair" ->
                pairBody(body, client.inetAddress?.hostAddress)
            else -> reply(400, errorBody("bad_request"))
        }
        writeResponse(client, response)
    }

    private fun infoBody(): String {
        val hostKey = DeviceIdentity.getOrCreateHostKeyPair()
        val fingerprint = DeviceIdentity.fingerprintSha256(hostKey.public as RSAPublicKey)
        return JSONObject().apply {
            put("v", 2)
            put("nonce", session.nonce)
            put("deviceid", DeviceIdentity.getDeviceId(context))
            put("name", Build.MODEL)
            put("type", "phone")
            put("fp", fingerprint)
            put("sftpport", SftpServerService.DEFAULT_PORT)
        }.toString()
    }

    private fun pairBody(rawBody: String, remoteIp: String?): HttpReply {
        if (session.state.value.state != PairingSession.State.PENDING) {
            return reply(409, errorBody("code_expired"))
        }
        val body = runCatching { JSONObject(rawBody) }.getOrNull()
            ?: return reply(400, errorBody("bad_request"))
        if (body.optInt("v", 0) != 2) return reply(400, errorBody("bad_request"))

        val code = body.optString("code").trim()
        val deviceId = body.optString("deviceid").trim()
        val name = body.optString("name").trim()
        val type = body.optString("type").trim()
        val hostFp = body.optString("hostFp").trim()
        val hostPub = body.optString("hostPub").trim()
        val clientPub = body.optString("clientPub").trim()

        val nonce = body.optString("nonce")
        val proof = body.optString("clientProof")
        val hostProof = body.optString("hostProof")
        val fieldsValid = CODE_PATTERN.matches(code) &&
            deviceId.isNotEmpty() && deviceId.length <= 64 && !deviceId.contains(':') &&
            name.isNotEmpty() && name.length <= 128 &&
            type in ALLOWED_TYPES &&
            listOf(deviceId, name, type, hostFp, hostPub, clientPub).none { it.contains('\n') || it.contains('\r') } &&
            nonce == session.nonce &&
            hostFp.startsWith("SHA256:") && hostFp.length <= MAX_FIELD_LENGTH &&
            isOpenSshKeyLine(hostPub) && isOpenSshKeyLine(clientPub)
        if (!fieldsValid) return reply(400, errorBody("bad_request"))

        if (code != session.code) {
            val stillActive = session.registerBadCode()
            return if (stillActive) {
                reply(401, errorBody("bad_code"))
            } else {
                reply(409, errorBody("code_expired"))
            }
        }

        val transcript = PairingProof.transcript(nonce, deviceId, name, type, hostFp, hostPub, clientPub)
        val hostPublic = runCatching { PairingProof.rsa(hostPub) }.getOrNull() ?: return reply(400, errorBody("bad_key"))
        val clientPublic = runCatching { PairingProof.rsa(clientPub) }.getOrNull() ?: return reply(400, errorBody("bad_key"))
        if (DeviceIdentity.fingerprintSha256(hostPublic) != hostFp ||
            !PairingProof.verify(clientPublic, transcript, proof) || !PairingProof.verify(hostPublic, transcript, hostProof))
            return reply(400, errorBody("bad_proof"))
        if (!session.requestApproval(name, DeviceIdentity.fingerprintSha256(clientPublic)))
            return reply(403, errorBody("not_approved"))
        if (!session.isPending) return reply(409, errorBody("code_expired"))

        val clientKeyBlob = clientPub.split(Regex("\\s+"))[1]
        val authorized = AuthorizedKeysStore(context)
        if (!authorized.add(clientPub, name.take(64))) {
            return reply(400, errorBody("bad_request"))
        }

        val saved = PairedDevice(
            deviceId = deviceId,
            name = name,
            type = type,
            hostKeyFingerprint = hostFp,
            lastKnownIp = remoteIp,
            lastKnownPort = SftpServerService.DEFAULT_PORT,
            lastSeenAt = System.currentTimeMillis(),
            peerKeyBlob = clientKeyBlob,
            peerHostPub = hostPub,
        )
        try { DeviceDirectory.getInstance(context).add(saved) }
        catch (error: Exception) { authorized.remove(clientKeyBlob); throw error }
        session.succeed(name)

        val hostKey = DeviceIdentity.getOrCreateHostKeyPair()
        val hostFpSelf = DeviceIdentity.fingerprintSha256(hostKey.public as RSAPublicKey)
        val hostPubSelf = DeviceIdentity.toOpenSshPublicKey(hostKey.public as RSAPublicKey)
        val clientPubSelf = ClientKey.openSshPublicKeyLine()
        val bodyJson = JSONObject().apply {
            put("ok", true)
            put("v", 2)
            put("nonce", nonce)
            put("deviceid", DeviceIdentity.getDeviceId(context))
            put("name", Build.MODEL)
            put("type", "phone")
            put("hostFp", hostFpSelf)
            put("hostPub", hostPubSelf)
            put("clientPub", clientPubSelf)
        }
        return reply(200, bodyJson.toString())
    }

    private fun isOpenSshKeyLine(line: String): Boolean {
        if (line.isBlank() || line.length > MAX_FIELD_LENGTH * 2) return false
        val parts = line.split(Regex("\\s+"))
        if (parts.size < 2 || !parts[0].startsWith("ssh-")) return false
        val blob = runCatching { java.util.Base64.getDecoder().decode(parts[1]) }.getOrNull()
        return blob != null && blob.size >= 32
    }

    private fun errorBody(error: String): String =
        JSONObject().apply {
            put("ok", false)
            put("error", error)
        }.toString()

    private data class HttpReply(val status: Int, val body: String)

    private fun reply(status: Int, body: String): HttpReply = HttpReply(status, body)

    private fun writeResponse(client: Socket, response: HttpReply) {
        val reason = when (response.status) {
            200 -> "OK"
            401 -> "Unauthorized"
            409 -> "Conflict"
            else -> "Bad Request"
        }
        val bytes = response.body.toByteArray(Charsets.UTF_8)
        val header = "HTTP/1.1 ${response.status} $reason\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            "Content-Length: ${bytes.size}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n"
        client.getOutputStream().use { output ->
            output.write(header.toByteArray(Charsets.US_ASCII))
            output.write(bytes)
            output.flush()
        }
    }

    private fun readLine(input: InputStream): String? {
        val builder = StringBuilder()
        while (true) {
            val b = input.read()
            if (b < 0) return if (builder.isEmpty()) null else builder.toString()
            if (b == '\n'.code) break
            if (b == '\r'.code) continue
            builder.append(b.toChar())
            if (builder.length > MAX_LINE_BYTES) return null
        }
        return builder.toString()
    }

    private fun readBody(input: InputStream, length: Int): String {
        val buffer = ByteArray(length)
        var offset = 0
        while (offset < length) {
            val read = input.read(buffer, offset, length - offset)
            if (read <= 0) break
            offset += read
        }
        return if (offset == length) buffer.toString(Charsets.UTF_8) else buffer.copyOf(offset).toString(Charsets.UTF_8)
    }
}

/**
 * 局域网地址枚举：配对服务只绑定 Wi-Fi / 热点等物理局域网接口。
 * 同时供设置页与二维码展示使用。
 */
object LanAddress {

    fun ipv4Addresses(): List<String> = candidates().map { it.second }

    /** 优先选择 wlan/wifi/ap 接口上的站点本地 IPv4 地址 */
    fun preferredIpv4(): String? {
        val list = candidates()
        val wifi = list.firstOrNull { (name, _) ->
            name.contains("wlan", ignoreCase = true) ||
                name.contains("wifi", ignoreCase = true) ||
                name.contains("ap", ignoreCase = true)
        }
        return (wifi ?: list.firstOrNull())?.second
    }

    private fun candidates(): List<Pair<String, String>> = runCatching {
        NetworkInterface.getNetworkInterfaces().toList()
            .filter { it.isUp && !it.isLoopback }
            .flatMap { iface ->
                iface.inetAddresses.toList()
                    .filter { !it.isLoopbackAddress && !it.isAnyLocalAddress && it.isSiteLocalAddress }
                    .mapNotNull { address: InetAddress ->
                        address.hostAddress?.takeIf { it.contains(':').not() }?.let { iface.name to it }
                    }
            }
            .distinctBy { it.second }
    }.getOrDefault(emptyList())
}
