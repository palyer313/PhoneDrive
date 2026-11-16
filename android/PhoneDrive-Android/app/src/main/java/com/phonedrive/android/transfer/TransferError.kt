package com.phonedrive.android.transfer

import java.io.FileNotFoundException
import java.io.IOException
import java.net.SocketException
import java.net.SocketTimeoutException
import java.nio.file.AccessDeniedException
import org.apache.sshd.sftp.common.SftpConstants
import org.apache.sshd.sftp.common.SftpException

/** Transport-level failure categories shared by both clients. */
enum class TransferErrorKind {
    NETWORK, NO_SPACE, PERMISSION, NOT_FOUND, CONFLICT, INTEGRITY, UNKNOWN
}

class TransferException(
    val kind: TransferErrorKind,
    message: String,
    cause: Throwable? = null,
) : IOException(message, cause)

object TransferErrorClassifier {
    fun classify(error: Throwable): TransferErrorKind {
        if (error is TransferException) return error.kind
        if (error is SecurityException || error is AccessDeniedException) return TransferErrorKind.PERMISSION
        if (isAuth(error)) return TransferErrorKind.PERMISSION
        if (isNoSpace(error)) return TransferErrorKind.NO_SPACE
        if (isIntegrity(error)) return TransferErrorKind.INTEGRITY
        if (isConflict(error)) return TransferErrorKind.CONFLICT

        var current: Throwable? = error
        var depth = 0
        while (current != null && depth < 8) {
            when (current) {
                is FileNotFoundException -> return TransferErrorKind.NOT_FOUND
                is SocketException, is SocketTimeoutException -> return TransferErrorKind.NETWORK
                is SftpException -> return when (current.status) {
                    SftpConstants.SSH_FX_NO_SUCH_FILE,
                    SftpConstants.SSH_FX_NO_SUCH_PATH -> TransferErrorKind.NOT_FOUND
                    SftpConstants.SSH_FX_PERMISSION_DENIED -> TransferErrorKind.PERMISSION
                    SftpConstants.SSH_FX_NO_SPACE_ON_FILESYSTEM -> TransferErrorKind.NO_SPACE
                    SftpConstants.SSH_FX_FILE_ALREADY_EXISTS -> TransferErrorKind.CONFLICT
                    else -> TransferErrorKind.NETWORK
                }
            }
            val name = current.javaClass.name
            val message = current.message.orEmpty()
            if (message.contains("permission denied", ignoreCase = true) ||
                message.contains("access denied", ignoreCase = true) ||
                message.contains("拒绝访问") || message.contains("权限不足")
            ) return TransferErrorKind.PERMISSION
            if (message.contains("no such file", ignoreCase = true) ||
                message.contains("not found", ignoreCase = true) ||
                message.contains("不存在")
            ) return TransferErrorKind.NOT_FOUND
            if (name.contains("SshException", ignoreCase = true)) return TransferErrorKind.NETWORK
            current = current.cause
            depth++
        }
        return TransferErrorKind.UNKNOWN
    }

    fun isAuth(error: Throwable): Boolean {
        var current: Throwable? = error
        var depth = 0
        while (current != null && depth < 8) {
            val name = current.javaClass.name
            val message = current.message.orEmpty()
            if (name.contains("AuthException", ignoreCase = true) ||
                message.contains("authentication", ignoreCase = true) ||
                message.contains("USERAUTH", ignoreCase = true) ||
                message.contains("publickey", ignoreCase = true)
            ) return true
            current = current.cause
            depth++
        }
        return false
    }

    fun isNoSpace(error: Throwable): Boolean {
        var current: Throwable? = error
        var depth = 0
        while (current != null && depth < 8) {
            val message = current.message.orEmpty()
            if (message.contains("no space", ignoreCase = true) ||
                message.contains("disk full", ignoreCase = true) ||
                message.contains("ENOSPC", ignoreCase = true) ||
                message.contains("空间不足") || message.contains("磁盘已满")
            ) return true
            current = current.cause
            depth++
        }
        return false
    }

    private fun isIntegrity(error: Throwable): Boolean {
        val message = error.message.orEmpty()
        return message.contains("SHA-256", ignoreCase = true) ||
            message.contains("hash", ignoreCase = true) ||
            message.contains("checksum", ignoreCase = true) ||
            message.contains("校验") || message.contains("完整性")
    }

    private fun isConflict(error: Throwable): Boolean {
        val message = error.message.orEmpty()
        return message.contains("目标已存在") || message.contains("同名") ||
            message.contains("already exists", ignoreCase = true) ||
            message.contains("conflict", ignoreCase = true)
    }
}
