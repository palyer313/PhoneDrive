package com.phonedrive.android.server

import android.content.Context
import com.phonedrive.android.security.DeviceIdentity
import java.io.File
import java.security.PublicKey
import java.security.interfaces.RSAPublicKey

/**
 * authorized_keys 管理：每行一个 OpenSSH 公钥（ssh-rsa AAAA... [comment]）。
 * 配对成功后写入对端客户端公钥；撤销授权时删除对应行。
 */
class AuthorizedKeysStore(context: Context) {

    private val file = File(context.filesDir, "authorized_keys")

    data class Entry(val keyType: String, val blobBase64: String, val comment: String)

    companion object {
        // 锁必须在类级别：各调用点都 new 新实例，实例锁完全失效，
        // add（追加）与 remove（读全量重写）并发会丢授权行
        private val LOCK = Any()
    }

    fun list(): List<Entry> {
        if (!file.exists()) return emptyList()
        return file.readLines()
            .map { it.trim() }
            .filter { it.isNotEmpty() && !it.startsWith("#") }
            .mapNotNull { line ->
                val parts = line.split(Regex("\\s+"), limit = 3)
                if (parts.size >= 2) {
                    Entry(parts[0], parts[1], parts.getOrElse(2) { "" })
                } else null
            }
    }

    fun add(openSshPublicKeyLine: String, comment: String): Boolean = synchronized(LOCK) {
        val parts = openSshPublicKeyLine.trim().split(Regex("\\s+"))
        if (runCatching { com.phonedrive.android.pairing.PairingProof.rsa(openSshPublicKeyLine) }.isFailure) return@synchronized false
        val normalized = "${parts[0]} ${parts[1]}"
        if (list().any { it.blobBase64 == parts[1] }) return@synchronized true // 幂等
        val line = if (comment.isBlank()) normalized else "$normalized $comment"
        val text = if (file.exists()) file.readText() else ""
        val temp = File(file.parentFile, "authorized_keys.tmp")
        temp.writeText(text.trimEnd() + (if (text.isBlank()) "" else "\n") + line + "\n")
        check(temp.renameTo(file)) { "授权文件写入失败" }
        return@synchronized true
    }

    fun remove(blobBase64: String): Unit = synchronized(LOCK) {
        SftpServerService.revokeConnections(blobBase64)
        if (!file.exists()) return@synchronized
        val kept = file.readLines().filter { line ->
            val parts = line.trim().split(Regex("\\s+"))
            !(parts.size >= 2 && parts[1] == blobBase64)
        }
        file.writeText(kept.joinToString("\n").let { if (it.isBlank()) "" else "$it\n" })
    }

    /** 判定客户端出示的公钥是否已授权（按 base64 blob 精确比对） */
    fun isAuthorized(key: PublicKey): Boolean {
        val presented = when (key) {
            is RSAPublicKey -> DeviceIdentity.toOpenSshPublicKey(key)
                .split(Regex("\\s+"))[1]
            else -> return false // Phase 1 仅接受 RSA 客户端密钥
        }
        return list().any { it.blobBase64 == presented }
    }
}
