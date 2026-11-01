package com.phonedrive.android.server

import android.content.Context
import android.os.Environment
import java.io.File

object SharePolicy {
    fun prefs(context: Context) = context.getSharedPreferences("phonedrive_sharing", Context.MODE_PRIVATE)
    fun root(context: Context): File {
        val base = Environment.getExternalStorageDirectory().canonicalFile
        val relative = prefs(context).getString("root", "") ?: ""
        require(!relative.split('/').any { it == ".." })
        val root = File(base, relative).canonicalFile
        require(root == base || root.path.startsWith(base.path + File.separator))
        require(!root.path.startsWith(File(base, "Android/data").path) && !root.path.startsWith(File(base, "Android/obb").path))
        require(root.isDirectory) { "共享目录不存在，请先创建或选择有效目录" }
        return root
    }
    fun readOnly(context: Context, blob: String?): Boolean = prefs(context).getBoolean("read_only", false) ||
        (blob != null && prefs(context).getBoolean("readonly_$blob", false))
}
