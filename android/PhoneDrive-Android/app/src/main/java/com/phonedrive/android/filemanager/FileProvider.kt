package com.phonedrive.android.filemanager

import java.io.File
import java.io.InputStream
import java.io.OutputStream

/**
 * 统一文件提供者：所有文件操作必须经由该接口，UI 层不得直接使用 File API。
 *
 * 路径约定：以共享根为 "/" 的逻辑绝对路径。
 */
interface FileProvider {

    val id: String
    val displayName: String

    fun list(path: String): List<FileEntry>

    fun stat(path: String): FileEntry

    fun openRead(path: String, offset: Long = 0L): InputStream

    fun openWrite(path: String, append: Boolean = false): OutputStream

    fun mkdir(path: String): Boolean

    /** 目录会被递归删除 */
    fun delete(path: String): Boolean

    fun deleteEmptyDirectory(path: String): Boolean

    fun rename(from: String, to: String): Boolean

    fun exists(path: String): Boolean

    /** 递归搜索（带结果上限），scope 为搜索起点目录 */
    fun search(keyword: String, scope: String): List<FileEntry>

    fun capabilities(): ProviderCapabilities

    /** 本地提供者返回真实 File，远端返回 null（用于系统 Intent / 解码预览） */
    fun localFile(path: String): File? = null

    companion object {
        fun join(dir: String, name: String): String = when {
            dir.isEmpty() || dir == "/" -> "/$name"
            else -> "$dir/$name"
        }

        fun parentOf(path: String): String {
            val trimmed = path.trimEnd('/')
            if (trimmed.isEmpty() || trimmed == "/") return "/"
            val idx = trimmed.lastIndexOf('/')
            return if (idx <= 0) "/" else trimmed.substring(0, idx)
        }

        fun nameOf(path: String): String {
            val trimmed = path.trimEnd('/')
            if (trimmed.isEmpty() || trimmed == "/") return "/"
            return trimmed.substringAfterLast('/')
        }
    }
}
