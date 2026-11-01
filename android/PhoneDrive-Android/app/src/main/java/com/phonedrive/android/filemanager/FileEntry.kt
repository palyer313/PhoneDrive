package com.phonedrive.android.filemanager

/** 文件类型图标分类，UI 层据此选择图标与筛选 */
enum class FileIconKind {
    FOLDER, IMAGE, VIDEO, AUDIO, DOCUMENT, ARCHIVE, APK, TEXT, OTHER
}

/**
 * 统一的文件条目抽象，本地共享存储与远端 SFTP 都映射到该结构。
 * path 为相对共享根的逻辑绝对路径（根为 "/"）。
 */
data class FileEntry(
    val name: String,
    val path: String,
    val size: Long,
    val modifiedAt: Long,
    val isDirectory: Boolean,
    val isSymlink: Boolean = false,
) {
    val iconKind: FileIconKind
        get() {
            if (isDirectory) return FileIconKind.FOLDER
            val ext = name.substringAfterLast('.', "").lowercase()
            return when (ext) {
                "jpg", "jpeg", "png", "gif", "webp", "bmp", "heic", "heif" -> FileIconKind.IMAGE
                "mp4", "mkv", "mov", "avi", "webm", "3gp", "flv", "ts" -> FileIconKind.VIDEO
                "mp3", "flac", "wav", "ogg", "aac", "m4a", "amr", "wma" -> FileIconKind.AUDIO
                "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "epub" -> FileIconKind.DOCUMENT
                "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso" -> FileIconKind.ARCHIVE
                "apk", "apks", "xapk", "aab" -> FileIconKind.APK
                "txt", "md", "log", "json", "xml", "yaml", "yml", "ini", "conf", "kt", "java",
                "py", "js", "ts", "c", "cpp", "h", "sh", "ps1", "html", "css" -> FileIconKind.TEXT
                else -> FileIconKind.OTHER
            }
        }
}

/** 提供者能力声明，UI 依据此隐藏不可用入口 */
data class ProviderCapabilities(
    val canWrite: Boolean = true,
    val canRename: Boolean = true,
    val canSearch: Boolean = true,
    val canRangeRead: Boolean = true,
)

/** 设备定位：deviceId 为 null 表示本机共享存储 */
data class StorageLocation(
    val deviceId: String?,
    val path: String,
) {
    companion object {
        fun local(path: String = "/") = StorageLocation(null, path)
    }
}
