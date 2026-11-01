package com.phonedrive.android.docs

import android.content.Context
import android.database.Cursor
import android.database.MatrixCursor
import android.os.CancellationSignal
import android.os.Handler
import android.os.HandlerThread
import android.os.ParcelFileDescriptor
import android.os.ProxyFileDescriptorCallback
import android.os.storage.StorageManager
import android.provider.DocumentsContract.Document
import android.provider.DocumentsContract.Root
import android.provider.DocumentsProvider
import android.system.ErrnoException
import android.system.OsConstants
import android.webkit.MimeTypeMap
import com.phonedrive.android.filemanager.FileEntry
import com.phonedrive.android.filemanager.FileProvider
import com.phonedrive.android.filemanager.ProviderHub
import com.phonedrive.android.filemanager.SftpFileProvider
import com.phonedrive.android.remote.DeviceDirectory
import java.io.FileNotFoundException
import java.io.IOException

/**
 * 系统 DocumentsProvider 集成（Phase 6）。
 *
 * 每台已配对设备暴露为一个 root（rootId = deviceId），文档通过
 * ProviderHub → SftpFileProvider 访问远端 SFTP。
 *
 * 随机读：使用 StorageManager.openProxyFileDescriptor + ProxyFileDescriptorCallback，
 * 按 offset/length 直接调用 SFTP 的 readRange，因此视频可以拖动进度。
 */
class PhoneDriveDocumentsProvider : DocumentsProvider() {

    companion object {
        private const val SEPARATOR = "::"
        private const val ROOT_PATH = "/"
        private const val OCTET_STREAM = "application/octet-stream"
        private const val MAX_UNIQUE_ATTEMPTS = 999

        private val DEFAULT_ROOT_PROJECTION = arrayOf(
            Root.COLUMN_ICON,
            Root.COLUMN_ROOT_ID,
            Root.COLUMN_DOCUMENT_ID,
            Root.COLUMN_TITLE,
            Root.COLUMN_FLAGS,
            Root.COLUMN_MIME_TYPES,
            Root.COLUMN_SUMMARY,
        )

        private val DEFAULT_DOCUMENT_PROJECTION = arrayOf(
            Document.COLUMN_DOCUMENT_ID,
            Document.COLUMN_DISPLAY_NAME,
            Document.COLUMN_MIME_TYPE,
            Document.COLUMN_SIZE,
            Document.COLUMN_LAST_MODIFIED,
            Document.COLUMN_FLAGS,
            Document.COLUMN_SUMMARY,
        )

        private const val ROOT_FLAGS = Root.FLAG_SUPPORTS_SEARCH
    }

    private val appContext: Context
        get() = requireNotNull(context) { "DocumentsProvider 尚未初始化" }.applicationContext

    private val directory: DeviceDirectory by lazy { DeviceDirectory.getInstance(appContext) }

    private val storageManager: StorageManager by lazy {
        appContext.getSystemService(Context.STORAGE_SERVICE) as StorageManager
    }

    @Volatile
    private var callbackThread: HandlerThread? = null

    @Volatile
    private var callbackHandler: Handler? = null

    override fun onCreate(): Boolean = true

    override fun shutdown() {
        callbackThread?.quitSafely()
        callbackThread = null
        callbackHandler = null
        super.shutdown()
    }

    // ---------------------------------------------------------------- 根目录

    override fun queryRoots(projection: Array<out String>?): Cursor {
        val columns = projection ?: DEFAULT_ROOT_PROJECTION
        val cursor = MatrixCursor(columns)
        for (device in directory.list().filter { it.type != "windows" }) {
            val row = cursor.newRow()
            if (columns.contains(Root.COLUMN_ICON)) row.add(Root.COLUMN_ICON, com.phonedrive.android.R.mipmap.ic_launcher)
            if (columns.contains(Root.COLUMN_ROOT_ID)) row.add(Root.COLUMN_ROOT_ID, device.deviceId)
            if (columns.contains(Root.COLUMN_DOCUMENT_ID)) {
                row.add(Root.COLUMN_DOCUMENT_ID, docId(device.deviceId, ROOT_PATH))
            }
            if (columns.contains(Root.COLUMN_TITLE)) row.add(Root.COLUMN_TITLE, device.name)
            if (columns.contains(Root.COLUMN_FLAGS)) row.add(Root.COLUMN_FLAGS, ROOT_FLAGS)
            if (columns.contains(Root.COLUMN_MIME_TYPES)) {
                row.add(Root.COLUMN_MIME_TYPES, "${Document.MIME_TYPE_DIR}\n*/*")
            }
            if (columns.contains(Root.COLUMN_SUMMARY)) {
                row.add(Root.COLUMN_SUMMARY, "已配对设备 · ${device.lastKnownIp ?: "等待发现"}")
            }
        }
        return cursor
    }

    // ---------------------------------------------------------------- 文档查询

    override fun queryDocument(documentId: String, projection: Array<out String>?): Cursor {
        val columns = projection ?: DEFAULT_DOCUMENT_PROJECTION
        val cursor = MatrixCursor(columns)
        val (deviceId, path) = splitDocumentId(documentId)
            ?: throw FileNotFoundException("无效的文档 ID：$documentId")
        val device = directory.find(deviceId)
            ?: throw FileNotFoundException("设备未配对或授权已撤销")
        if (path == ROOT_PATH) {
            addDocumentRow(
                cursor = cursor,
                columns = columns,
                documentId = docId(deviceId, ROOT_PATH),
                displayName = device.name,
                size = 0L,
                modifiedAt = 0L,
                isDirectory = true,
                isRoot = true,
            )
            return cursor
        }
        val entry = try {
            providerFor(deviceId).stat(path)
        } catch (e: Exception) {
            throw FileNotFoundException("无法读取文档：$path")
        }
        addDocumentRow(cursor, columns, deviceId, entry)
        return cursor
    }

    override fun queryChildDocuments(
        parentDocumentId: String,
        projection: Array<out String>?,
        sortOrder: String?,
    ): Cursor {
        val columns = projection ?: DEFAULT_DOCUMENT_PROJECTION
        val cursor = MatrixCursor(columns)
        val (deviceId, parentPath) = splitDocumentId(parentDocumentId)
            ?: throw FileNotFoundException("无效的父目录 ID：$parentDocumentId")
        val device = directory.find(deviceId)
            ?: throw FileNotFoundException("设备未配对或授权已撤销")
        val children = try {
            providerFor(deviceId).list(parentPath)
        } catch (e: Exception) {
            throw FileNotFoundException("无法读取目录：$parentPath")
        }
        val sorted = children.sortedWith(
            compareByDescending<FileEntry> { it.isDirectory }.thenBy { it.name.lowercase() }
        )
        for (child in sorted) {
            addDocumentRow(cursor, columns, deviceId, child)
        }
        return cursor
    }

    override fun querySearchDocuments(
        rootId: String,
        query: String,
        projection: Array<out String>?,
    ): Cursor {
        val columns = projection ?: DEFAULT_DOCUMENT_PROJECTION
        val cursor = MatrixCursor(columns)
        val deviceId = rootId.substringBefore(SEPARATOR)
        val device = directory.find(deviceId) ?: return cursor
        val keyword = query.trim()
        if (keyword.isEmpty()) return cursor
        val results = runCatching { providerFor(deviceId).search(keyword, ROOT_PATH) }
            .getOrDefault(emptyList())
        for (entry in results) {
            addDocumentRow(cursor, columns, deviceId, entry)
        }
        return cursor
    }

    override fun isChildDocument(parentDocumentId: String, documentId: String): Boolean {
        val parent = splitDocumentId(parentDocumentId) ?: return false
        val child = splitDocumentId(documentId) ?: return false
        if (parent.first != child.first) return false
        val parentPath = parent.second.trimEnd('/')
        if (parentPath.isEmpty()) return true
        val childPath = child.second
        return childPath == parentPath || childPath.startsWith("$parentPath/")
    }

    override fun getDocumentType(documentId: String): String {
        val (deviceId, path) = splitDocumentId(documentId) ?: return OCTET_STREAM
        if (path == ROOT_PATH) return Document.MIME_TYPE_DIR
        val entry = runCatching { providerFor(deviceId).stat(path) }.getOrNull()
            ?: return OCTET_STREAM
        return mimeTypeOf(entry.name, entry.isDirectory)
    }

    // ---------------------------------------------------------------- 打开文件

    override fun openDocument(
        documentId: String,
        mode: String,
        signal: CancellationSignal?,
    ): ParcelFileDescriptor {
        val (deviceId, path) = splitDocumentId(documentId)
            ?: throw FileNotFoundException("无效的文档 ID：$documentId")
        val provider = providerFor(deviceId)
        val entry = try {
            provider.stat(path)
        } catch (e: Exception) {
            throw FileNotFoundException("无法读取文档：$path")
        }
        if (entry.isDirectory) throw FileNotFoundException("目录不能作为文件打开：$path")

        if (mode !in setOf("r", "w", "wt", "wa", "rw", "rwt")) throw FileNotFoundException("不支持的打开模式")
        val append = mode == "wa"
        val truncate = mode == "w" || mode == "wt" || mode == "rwt"
        val writable = mode.contains('w') || append

        if (truncate) {
            provider.openWrite(path, append = false).close()
        }

        val flags = when {
            mode == "r" -> ParcelFileDescriptor.MODE_READ_ONLY
            mode == "w" || mode == "wt" -> ParcelFileDescriptor.MODE_WRITE_ONLY
            append -> ParcelFileDescriptor.MODE_WRITE_ONLY
            else -> ParcelFileDescriptor.MODE_READ_WRITE
        }

        val callback = RemoteFileCallback(provider, path, writable, append)
        return try {
            storageManager.openProxyFileDescriptor(flags, callback, callbackHandler())
        } catch (e: IOException) {
            throw FileNotFoundException("无法打开文档（$mode）：$path")
        }
    }

    private fun callbackHandler(): Handler {
        callbackHandler?.let { return it }
        synchronized(this) {
            callbackHandler?.let { return it }
            val thread = HandlerThread("phonedrive-documents-io").apply { start() }
            callbackThread = thread
            val handler = Handler(thread.looper)
            callbackHandler = handler
            return handler
        }
    }

    private class RemoteFileCallback(
        private val provider: SftpFileProvider,
        private val path: String,
        private val writable: Boolean,
        private val append: Boolean,
    ) : ProxyFileDescriptorCallback() {

        override fun onGetSize(): Long = provider.stat(path).size

        override fun onRead(offset: Long, size: Int, data: ByteArray): Int {
            if (size <= 0) return 0
            val bytes = try {
                provider.readRange(path, offset, size)
            } catch (e: Exception) {
                throw ErrnoException("read", OsConstants.EIO)
            }
            if (bytes.isEmpty()) return 0
            val count = minOf(bytes.size, size, data.size)
            bytes.copyInto(data, 0, 0, count)
            return count
        }

        override fun onWrite(offset: Long, size: Int, data: ByteArray): Int {
            if (!writable) throw ErrnoException("write", OsConstants.EPERM)
            if (size <= 0) return 0
            try {
                provider.writeRange(path, if (append) provider.stat(path).size else offset, data, size)
            } catch (e: Exception) {
                throw ErrnoException("write", OsConstants.EIO)
            }
            return size
        }

        override fun onFsync() = Unit

        override fun onRelease() = Unit
    }

    // ---------------------------------------------------------------- 写操作

    override fun createDocument(
        parentDocumentId: String,
        mimeType: String,
        displayName: String,
    ): String {
        val (deviceId, parentPath) = splitDocumentId(parentDocumentId)
            ?: throw FileNotFoundException("无效的父目录 ID：$parentDocumentId")
        val provider = providerFor(deviceId)
        val name = sanitizeName(displayName)
        if (name.isEmpty()) throw FileNotFoundException("文件名不能为空")
        val targetPath = uniqueChildPath(provider, parentPath, name)

        if (mimeType == Document.MIME_TYPE_DIR) {
            provider.mkdir(targetPath)
        } else {
            provider.openWrite(targetPath, append = false).close()
        }
        return docId(deviceId, targetPath)
    }

    override fun deleteDocument(documentId: String) {
        val (deviceId, path) = splitDocumentId(documentId)
            ?: throw FileNotFoundException("无效的文档 ID：$documentId")
        if (path == ROOT_PATH) throw FileNotFoundException("不能删除设备根目录")
        val provider = providerFor(deviceId)
        if (!provider.delete(path)) throw FileNotFoundException("删除失败：$path")
    }

    override fun renameDocument(documentId: String, displayName: String): String {
        val (deviceId, path) = splitDocumentId(documentId)
            ?: throw FileNotFoundException("无效的文档 ID：$documentId")
        if (path == ROOT_PATH) throw FileNotFoundException("不能重命名设备根目录")
        val name = sanitizeName(displayName)
        if (name.isEmpty()) throw FileNotFoundException("文件名不能为空")
        val provider = providerFor(deviceId)
        val parent = FileProvider.parentOf(path)
        val targetPath = FileProvider.join(parent, name)
        if (targetPath == path) return documentId
        if (provider.exists(targetPath)) throw FileNotFoundException("同名文件已存在：$name")
        if (!provider.rename(path, targetPath)) throw FileNotFoundException("重命名失败：$path")
        return docId(deviceId, targetPath)
    }

    // ---------------------------------------------------------------- 工具

    private fun providerFor(deviceId: String): SftpFileProvider {
        val device = directory.find(deviceId)
            ?: throw FileNotFoundException("设备未配对或授权已撤销")
        val provider = try {
            ProviderHub.providerForBlocking(deviceId)
        } catch (e: Exception) {
            throw FileNotFoundException("无法连接设备「${device.name}」：${e.message ?: "网络错误"}")
        }
        return provider as? SftpFileProvider
            ?: throw FileNotFoundException("设备「${device.name}」不可用")
    }

    private fun addDocumentRow(
        cursor: MatrixCursor,
        columns: Array<out String>,
        deviceId: String,
        entry: FileEntry,
    ) {
        addDocumentRow(
            cursor = cursor,
            columns = columns,
            documentId = docId(deviceId, entry.path),
            displayName = entry.name,
            size = entry.size,
            modifiedAt = entry.modifiedAt,
            isDirectory = entry.isDirectory,
            isRoot = false,
        )
    }

    private fun addDocumentRow(
        cursor: MatrixCursor,
        columns: Array<out String>,
        documentId: String,
        displayName: String,
        size: Long,
        modifiedAt: Long,
        isDirectory: Boolean,
        isRoot: Boolean,
    ) {
        val row = cursor.newRow()
        if (columns.contains(Document.COLUMN_DOCUMENT_ID)) {
            row.add(Document.COLUMN_DOCUMENT_ID, documentId)
        }
        if (columns.contains(Document.COLUMN_DISPLAY_NAME)) {
            row.add(Document.COLUMN_DISPLAY_NAME, displayName)
        }
        if (columns.contains(Document.COLUMN_MIME_TYPE)) {
            row.add(Document.COLUMN_MIME_TYPE, mimeTypeOf(displayName, isDirectory))
        }
        if (columns.contains(Document.COLUMN_SIZE) && !isDirectory) {
            row.add(Document.COLUMN_SIZE, size)
        }
        if (columns.contains(Document.COLUMN_LAST_MODIFIED) && !isRoot) {
            row.add(Document.COLUMN_LAST_MODIFIED, modifiedAt)
        }
        if (columns.contains(Document.COLUMN_FLAGS)) {
            row.add(Document.COLUMN_FLAGS, documentFlags(isDirectory))
        }
        if (columns.contains(Document.COLUMN_SUMMARY) && !isRoot) {
            row.add(Document.COLUMN_SUMMARY, null)
        }
    }

    private fun documentFlags(isDirectory: Boolean): Int = if (isDirectory) {
        Document.FLAG_DIR_SUPPORTS_CREATE or
            Document.FLAG_SUPPORTS_DELETE or
            Document.FLAG_SUPPORTS_RENAME
    } else {
        Document.FLAG_SUPPORTS_WRITE or
            Document.FLAG_SUPPORTS_DELETE or
            Document.FLAG_SUPPORTS_RENAME
    }

    private fun mimeTypeOf(name: String, isDirectory: Boolean): String {
        if (isDirectory) return Document.MIME_TYPE_DIR
        val extension = name.substringAfterLast('.', "").lowercase()
        return MimeTypeMap.getSingleton().getMimeTypeFromExtension(extension) ?: OCTET_STREAM
    }

    private fun sanitizeName(displayName: String): String =
        displayName.trim().replace('/', '_').replace('\\', '_').also { require(it != "." && it != "..") }

    private fun uniqueChildPath(provider: FileProvider, parentPath: String, name: String): String {
        val initial = FileProvider.join(parentPath, name)
        if (!provider.exists(initial)) return initial
        val dot = name.lastIndexOf('.')
        val stem = if (dot > 0) name.substring(0, dot) else name
        val extension = if (dot > 0) name.substring(dot) else ""
        for (index in 2..MAX_UNIQUE_ATTEMPTS) {
            val candidate = FileProvider.join(parentPath, "$stem ($index)$extension")
            if (!provider.exists(candidate)) return candidate
        }
        throw FileNotFoundException("同名文件过多，无法自动重命名：$name")
    }

    private fun docId(deviceId: String, path: String): String = "$deviceId$SEPARATOR$path"

    /** 返回 deviceId 与逻辑路径；兼容直接传入 rootId 的情况 */
    private fun splitDocumentId(documentId: String): Pair<String, String>? {
        val index = documentId.indexOf(SEPARATOR)
        if (index < 0) {
            return if (directory.find(documentId) != null) documentId to ROOT_PATH else null
        }
        val deviceId = documentId.substring(0, index)
        val path = documentId.substring(index + SEPARATOR.length).ifBlank { ROOT_PATH }
        if (deviceId.isBlank() || !path.startsWith('/') || path.split('/').any { it == ".." || it == "." } || path.contains('\\')) return null
        return deviceId to path
    }
}
