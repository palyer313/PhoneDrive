package com.phonedrive.android.ui

import android.content.Context
import android.content.Intent
import android.provider.DocumentsContract
import android.webkit.MimeTypeMap
import androidx.core.content.FileProvider
import com.phonedrive.android.filemanager.FileEntry
import com.phonedrive.android.filemanager.ProviderHub

/** Expose a seekable content URI, never pre-download a complete remote video. */
fun openExternalFile(context: Context, deviceId: String?, entry: FileEntry, edit: Boolean = false) {
    val uri = if (deviceId == null) {
        val file = ProviderHub.local().localFile(entry.path) ?: error("本地路径不可用")
        FileProvider.getUriForFile(context, "${context.packageName}.files", file)
    } else DocumentsContract.buildDocumentUri("${context.packageName}.documents", "$deviceId::${entry.path}")
    val mime = MimeTypeMap.getSingleton().getMimeTypeFromExtension(entry.name.substringAfterLast('.', "").lowercase()) ?: "application/octet-stream"
    val intent = Intent(if (edit) Intent.ACTION_EDIT else Intent.ACTION_VIEW).apply {
        setDataAndType(uri, mime)
        addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        if (edit) addFlags(Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
    }
    context.startActivity(Intent.createChooser(intent, if (edit) "选择编辑应用（修改会保存到原位置）" else "选择打开应用"))
}
