package com.phonedrive.android.filemanager

import android.content.Context
import android.os.Environment
import java.io.File
import java.io.FileInputStream
import java.io.FileOutputStream
import java.io.InputStream
import java.io.OutputStream

/**
 * 本地共享存储提供者。
 * root = Environment.getExternalStorageDirectory()，逻辑路径 "/" 即共享根。
 * 需要 MANAGE_EXTERNAL_STORAGE（Android 11+）或 READ/WRITE 权限，由设置页引导授权。
 */
class LocalFileProvider(@Suppress("UNUSED_PARAMETER") context: Context) : FileProvider {

    companion object {
        private const val SEARCH_LIMIT = 300
    }

    private val root: File = Environment.getExternalStorageDirectory()

    override val id: String = "local"
    override val displayName: String = "本地存储"

    override fun list(path: String): List<FileEntry> {
        val dir = resolve(path)
        if (!dir.exists()) throw IllegalArgumentException("目录不存在：$path")
        if (!dir.isDirectory) throw IllegalArgumentException("不是目录：$path")
        val children = dir.listFiles()
            ?: throw IllegalStateException("无法读取目录，请在设置中授予\"所有文件访问\"权限")
        return children.map { toEntry(it) }
    }

    override fun stat(path: String): FileEntry {
        val file = resolve(path)
        if (!file.exists()) throw IllegalArgumentException("文件不存在：$path")
        return toEntry(file)
    }

    override fun openRead(path: String, offset: Long): InputStream {
        val file = resolve(path)
        if (!file.exists()) throw IllegalArgumentException("文件不存在：$path")
        val stream = FileInputStream(file)
        if (offset > 0) stream.channel.position(offset)
        return stream
    }

    override fun openWrite(path: String, append: Boolean): OutputStream {
        val file = resolve(path)
        file.parentFile?.let { if (!it.exists()) it.mkdirs() }
        return FileOutputStream(file, append)
    }

    override fun mkdir(path: String): Boolean {
        val dir = resolve(path)
        return dir.exists() || dir.mkdirs()
    }

    override fun delete(path: String): Boolean {
        val target = resolve(path)
        if (target == root) return false
        return if (target.isDirectory) target.deleteRecursively() else target.delete()
    }

    override fun deleteEmptyDirectory(path: String): Boolean = resolve(path).let { it != root && it.delete() }

    override fun rename(from: String, to: String): Boolean {
        val source = resolve(from)
        val target = resolve(to)
        if (!source.exists()) return false
        if (target.exists()) return false
        target.parentFile?.let { if (!it.exists()) it.mkdirs() }
        return source.renameTo(target)
    }

    override fun exists(path: String): Boolean = resolve(path).exists()

    override fun search(keyword: String, scope: String): List<FileEntry> {
        val query = keyword.trim()
        if (query.isEmpty()) return emptyList()
        val start = resolve(scope)
        if (!start.exists()) return emptyList()
        val result = mutableListOf<FileEntry>()
        val queue = ArrayDeque<File>()
        queue.add(start)
        var visitedDirs = 0
        while (queue.isNotEmpty() && result.size < SEARCH_LIMIT && visitedDirs < 2000) {
            val dir = queue.removeFirst()
            visitedDirs++
            val children = dir.listFiles() ?: continue
            for (child in children) {
                if (child.isDirectory) {
                    queue.add(child)
                }
                if (child.name.contains(query, ignoreCase = true)) {
                    result.add(toEntry(child))
                    if (result.size >= SEARCH_LIMIT) break
                }
            }
        }
        return result
    }

    override fun capabilities(): ProviderCapabilities = ProviderCapabilities()

    override fun localFile(path: String): File = resolve(path)

    private fun resolve(path: String): File {
        val clean = path.trim().trimStart('/')
        if (clean.isEmpty()) return root
        require(!clean.split('/').any { it == ".." }) { "非法路径：$path" }
        return File(root, clean)
    }

    private fun toEntry(file: File): FileEntry {
        val isDir = file.isDirectory
        return FileEntry(
            name = if (file == root) "/" else file.name,
            path = relative(file),
            size = if (isDir) 0L else file.length(),
            modifiedAt = file.lastModified(),
            isDirectory = isDir,
            isSymlink = runCatching {
                java.nio.file.Files.isSymbolicLink(file.toPath())
            }.getOrDefault(false),
        )
    }

    private fun relative(file: File): String {
        val rootPath = root.absolutePath
        val abs = file.absolutePath
        return if (abs == rootPath) "/" else "/" + abs.removePrefix(rootPath).trimStart('/')
    }
}
