package com.phonedrive.android.remote

import com.phonedrive.android.security.DeviceIdentity
import org.apache.sshd.client.SshClient
import org.apache.sshd.client.keyverifier.ServerKeyVerifier
import org.apache.sshd.client.session.ClientSession
import org.apache.sshd.sftp.client.SftpClient
import org.apache.sshd.sftp.client.SftpClientFactory
import java.io.Closeable
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.security.PublicKey
import java.security.interfaces.RSAPublicKey
import java.util.concurrent.TimeUnit

/**
 * 指向一台已配对设备的 SFTP 客户端会话。
 *
 * 安全约束：
 * - 仅 publickey 认证，客户端密钥为 Android Keystore 中的 phonedrive_client_key；
 * - 连接建立阶段强制校验服务端 Host Key 指纹，与配对记录严格 equals 比对，
 *   不匹配立即失败（不存在 StrictHostKeyChecking=no 的降级路径）。
 */
class SftpClientSession(private val device: PairedDevice) : Closeable {

    companion object {
        private const val USERNAME = "phonedrive"
        private const val DEFAULT_TIMEOUT_MS = 15_000L
        private const val WRITE_BUFFER = 32 * 1024
    }

    data class SftpFileInfo(
        val name: String,
        val path: String,
        val size: Long,
        val modifiedAt: Long,
        val isDirectory: Boolean,
        val isSymlink: Boolean,
    )

    private var client: SshClient? = null
    private var session: ClientSession? = null
    private var sftp: SftpClient? = null

    @Volatile
    private var hostKeyError: String? = null

    @Volatile
    var verifiedFingerprint: String? = null
        private set

    fun isConnected(): Boolean = sftp?.let { !it.isClosing } == true

    fun remoteHost(): String? = device.lastKnownIp

    @Synchronized
    fun connect(timeoutMillis: Long = DEFAULT_TIMEOUT_MS) {
        if (isConnected()) return
        closeSession()

        val host = device.lastKnownIp
            ?: throw IllegalStateException("设备「${device.name}」缺少已知 IP，请等待设备重新出现在发现列表中")
        if (device.hostKeyFingerprint.isBlank()) {
            throw IllegalStateException("设备「${device.name}」未记录 Host Key 指纹，拒绝连接")
        }

        hostKeyError = null
        val sshClient = SshClient.setUpDefaultClient()
        sshClient.signatureFactories = ClientKey.buildSignatureFactories(sshClient.signatureFactories)
        sshClient.serverKeyVerifier = ServerKeyVerifier { _, _, key -> verifyServerKey(key) }
        try {
            sshClient.start()
            val connectFuture = sshClient.connect(USERNAME, host, device.lastKnownPort)
            val newSession = connectFuture.verify(timeoutMillis, TimeUnit.MILLISECONDS).session
            newSession.addPublicKeyIdentity(ClientKey.getOrCreateKeyPair())
            newSession.auth().verify(timeoutMillis, TimeUnit.MILLISECONDS)
            val sftpClient = SftpClientFactory.instance().createSftpClient(newSession)
            client = sshClient
            session = newSession
            sftp = sftpClient
        } catch (e: Exception) {
            runCatching { sshClient.stop() }
            client = null
            session = null
            sftp = null
            val message = hostKeyError
            if (message != null) {
                throw IllegalStateException(message, e)
            }
            throw IllegalStateException(
                "连接「${device.name}」失败：${e.message ?: e.javaClass.simpleName}", e
            )
        }
    }

    private fun verifyServerKey(key: PublicKey): Boolean {
        if (key !is RSAPublicKey) {
            hostKeyError = "服务端 Host Key 类型为 ${key.algorithm}，与协议要求（RSA）不符，已拒绝连接"
            return false
        }
        val actual = DeviceIdentity.fingerprintSha256(key)
        if (!actual.equals(device.hostKeyFingerprint, ignoreCase = false)) {
            hostKeyError = "严重警告：服务端 Host Key 指纹不匹配！\n" +
                "已记录：${device.hostKeyFingerprint}\n实际：$actual\n" +
                "可能遭遇中间人攻击，已中止连接。"
            return false
        }
        verifiedFingerprint = actual
        return true
    }

    @Synchronized
    fun disconnect() {
        closeSession()
    }

    override fun close() = disconnect()

    private fun closeSession() {
        runCatching { sftp?.close() }
        sftp = null
        runCatching { session?.close(false) }
        session = null
        runCatching { client?.stop() }
        client = null
    }

    private fun requireSftp(): SftpClient = sftp ?: throw IllegalStateException(
        "与「${device.name}」的连接已断开，请重新连接"
    )

    fun listDirectory(path: String): List<SftpFileInfo> {
        val dir = normalize(path)
        val entries = requireSftp().readEntries(dir)
        return entries.mapNotNull { entry ->
            val name = entry.filename ?: return@mapNotNull null
            if (name == "." || name == "..") return@mapNotNull null
            val attrs = entry.attributes
            SftpFileInfo(
                name = name,
                path = join(dir, name),
                size = attrs.size,
                modifiedAt = attrs.modifyTime?.toMillis() ?: 0L,
                isDirectory = attrs.isDirectory,
                isSymlink = attrs.isSymbolicLink,
            )
        }
    }

    fun getFileInfo(path: String): SftpFileInfo? {
        val normalized = normalize(path)
        val attrs = runCatching { requireSftp().lstat(normalized) }.getOrNull() ?: return null
        return SftpFileInfo(
            name = normalized.substringAfterLast('/').ifBlank { "/" },
            path = normalized,
            size = attrs.size,
            modifiedAt = attrs.modifyTime?.toMillis() ?: 0L,
            isDirectory = attrs.isDirectory,
            isSymlink = attrs.isSymbolicLink,
        )
    }

    fun openRead(path: String, offset: Long): InputStream {
        val normalized = normalize(path)
        val handle = requireSftp().open(normalized, listOf(SftpClient.OpenMode.Read))
        return SftpRemoteInputStream(requireSftp(), handle, offset)
    }

    fun openWrite(path: String, append: Boolean): OutputStream {
        val modes = if (append) {
            listOf(SftpClient.OpenMode.Write, SftpClient.OpenMode.Create, SftpClient.OpenMode.Append)
        } else {
            listOf(SftpClient.OpenMode.Write, SftpClient.OpenMode.Create, SftpClient.OpenMode.Truncate)
        }
        return requireSftp().write(normalize(path), WRITE_BUFFER, modes)
    }

    /** 定位写入：用于需要显式偏移的调用方 */
    fun write(path: String, offset: Long, data: ByteArray, length: Int) {
        val normalized = normalize(path)
        val client = requireSftp()
        val handle = client.open(
            normalized,
            listOf(SftpClient.OpenMode.Write, SftpClient.OpenMode.Create)
        )
        try {
            client.write(handle, offset, data, 0, length)
        } finally {
            runCatching { client.close(handle) }
        }
    }

    fun createDirectory(path: String) {
        requireSftp().mkdir(normalize(path))
    }

    fun delete(path: String) {
        val normalized = normalize(path)
        val client = requireSftp()
        val attrs = runCatching { client.lstat(normalized) }.getOrNull()
        if (attrs?.isDirectory == true && !attrs.isSymbolicLink) {
            // rmdir 只能删空目录；非空目录必须先递归清空，
            // 否则删除远端非空文件夹、跨设备移动目录、覆盖目录都会失败
            deleteTree(client, normalized, 0)
        } else {
            client.remove(normalized)
        }
    }

    private fun deleteTree(client: org.apache.sshd.sftp.client.SftpClient, dir: String, depth: Int) {
        if (depth > 32) throw java.io.IOException("目录层级过深，无法删除：$dir")
        for (entry in client.readDir(dir)) {
            val name = entry.filename ?: continue
            if (name == "." || name == "..") continue
            val child = if (dir.endsWith("/")) dir + name else "$dir/$name"
            val childAttrs = runCatching { client.lstat(child) }.getOrNull() ?: continue
            if (childAttrs.isDirectory && !childAttrs.isSymbolicLink) {
                deleteTree(client, child, depth + 1)
            } else {
                client.remove(child)
            }
        }
        client.rmdir(dir)
    }

    fun deleteEmptyDirectory(path: String) { requireSftp().rmdir(normalize(path)) }

    fun rename(from: String, to: String) {
        requireSftp().rename(normalize(from), normalize(to))
    }

    fun move(from: String, to: String) = rename(from, to)

    fun readRange(path: String, offset: Long, length: Int): ByteArray {
        val normalized = normalize(path)
        val client = requireSftp()
        val handle = client.open(normalized, listOf(SftpClient.OpenMode.Read))
        try {
            if (length <= 0) return ByteArray(0)
            val buffer = ByteArray(length)
            var total = 0
            var position = offset
            while (total < length) {
                val n = client.read(handle, position, buffer, total, length - total)
                if (n <= 0) break
                total += n
                position += n
            }
            return if (total == length) buffer else buffer.copyOf(total)
        } finally {
            runCatching { client.close(handle) }
        }
    }

    fun canonicalPath(path: String): String {
        val normalized = normalize(path)
        return runCatching { requireSftp().canonicalPath(normalized) }.getOrDefault(normalized)
    }

    private fun normalize(path: String): String {
        val trimmed = path.trim()
        if (trimmed.isEmpty() || trimmed == "/") return "/"
        return if (trimmed.startsWith("/")) trimmed else "/$trimmed"
    }

    private fun join(dir: String, name: String): String =
        if (dir == "/" || dir.isEmpty()) "/$name" else "$dir/$name"

    /**
     * SFTP 句柄流：按偏移顺序读取，避免整文件进内存。
     * 所有调用均发生在引擎的 IO 线程上，句柄不做跨线程共享。
     */
    private class SftpRemoteInputStream(
        private val client: SftpClient,
        private val handle: SftpClient.CloseableHandle,
        startOffset: Long,
    ) : InputStream() {

        private var position: Long = startOffset
        private var closed = false

        override fun read(): Int {
            val one = ByteArray(1)
            val n = read(one, 0, 1)
            return if (n <= 0) -1 else one[0].toInt() and 0xFF
        }

        override fun read(b: ByteArray, off: Int, len: Int): Int {
            if (closed) throw IOException("流已关闭")
            if (len == 0) return 0
            val n = client.read(handle, position, b, off, len)
            if (n <= 0) return -1
            position += n
            return n
        }

        override fun close() {
            if (closed) return
            closed = true
            runCatching { client.close(handle) }
        }
    }
}
