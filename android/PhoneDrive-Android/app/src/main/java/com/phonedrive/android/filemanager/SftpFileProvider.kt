package com.phonedrive.android.filemanager

import com.phonedrive.android.remote.SftpClientSession
import java.io.InputStream
import java.io.OutputStream

/**
 * 远端设备提供者：包装 SftpClientSession，向 UI 暴露与本地一致的文件操作。
 */
class SftpFileProvider(
    private val session: SftpClientSession,
    val deviceId: String,
    private val deviceName: String,
) : FileProvider {

    companion object {
        private const val SEARCH_LIMIT = 300
    }

    override val id: String = "sftp:$deviceId"
    override val displayName: String = deviceName

    val isConnected: Boolean get() = session.isConnected()

    fun disconnect() = session.disconnect()

    override fun list(path: String): List<FileEntry> =
        session.listDirectory(path).map { it.toEntry() }

    override fun stat(path: String): FileEntry =
        session.getFileInfo(path)?.toEntry() ?: throw IllegalArgumentException("文件不存在：$path")

    override fun openRead(path: String, offset: Long): InputStream =
        session.openRead(path, offset)

    override fun openWrite(path: String, append: Boolean): OutputStream =
        session.openWrite(path, append)

    override fun mkdir(path: String): Boolean {
        session.createDirectory(path)
        return true
    }

    override fun delete(path: String): Boolean {
        session.delete(path)
        return true
    }

    override fun deleteEmptyDirectory(path: String): Boolean { session.deleteEmptyDirectory(path); return true }

    override fun rename(from: String, to: String): Boolean {
        session.rename(from, to)
        return true
    }

    override fun exists(path: String): Boolean = session.getFileInfo(path) != null

    override fun search(keyword: String, scope: String): List<FileEntry> {
        val query = keyword.trim()
        if (query.isEmpty()) return emptyList()
        val result = mutableListOf<FileEntry>()
        val queue = ArrayDeque<String>()
        queue.add(scope)
        var visitedDirs = 0
        while (queue.isNotEmpty() && result.size < SEARCH_LIMIT && visitedDirs < 2000) {
            val dir = queue.removeFirst()
            visitedDirs++
            val children = try {
                session.listDirectory(dir)
            } catch (e: Exception) {
                continue
            }
            for (child in children) {
                if (child.isDirectory && !child.isSymlink) queue.add(child.path)
                if (child.name.contains(query, ignoreCase = true)) {
                    result.add(child.toEntry())
                    if (result.size >= SEARCH_LIMIT) break
                }
            }
        }
        return result
    }

    override fun capabilities(): ProviderCapabilities = ProviderCapabilities()

    /** 随机读取指定区间（DocumentsProvider 拖动进度依赖） */
    fun readRange(path: String, offset: Long, length: Int): ByteArray =
        session.readRange(path, offset, length)

    /** 按偏移随机写入，用于 ProxyFileDescriptorCallback */
    fun writeRange(path: String, offset: Long, data: ByteArray, length: Int) =
        session.write(path, offset, data, length)

    private fun SftpClientSession.SftpFileInfo.toEntry(): FileEntry = FileEntry(
        name = name,
        path = path,
        size = if (isDirectory) 0L else size,
        modifiedAt = modifiedAt,
        isDirectory = isDirectory,
        isSymlink = isSymlink,
    )
}
