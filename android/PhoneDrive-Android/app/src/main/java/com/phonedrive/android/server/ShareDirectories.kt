package com.phonedrive.android.server

import java.io.File

object ShareDirectories {
    fun validate(baseDirectory: File, directory: File): File {
        val base = baseDirectory.canonicalFile
        val target = directory.canonicalFile
        require(target == base || target.path.startsWith(base.path + File.separator)) { "只能选择内部共享存储中的文件夹" }
        for (name in listOf("Android/data", "Android/obb")) {
            val restricted = File(base, name).canonicalFile
            require(target != restricted && !target.path.startsWith(restricted.path + File.separator)) { "系统不允许共享此目录" }
        }
        require(target.isDirectory && target.canRead()) { "目录不存在或没有读取权限" }
        return target
    }

    fun create(base: File, parent: File, name: String): File {
        require(name.isNotBlank() && name == name.trim() && name != "." && name != ".." &&
            name.none { it == '/' || it == '\\' || it.code < 32 }) { "请输入有效的单个文件夹名称，不可包含路径分隔符" }
        val directory = validate(base, parent)
        val target = File(directory, name)
        require(!target.exists()) { "同名文件或文件夹已存在" }
        // Validate the candidate boundary before creating, including restricted Android directories.
        val canonical = target.canonicalFile
        require(canonical.parentFile == directory) { "目录超出当前范围" }
        for (restricted in listOf(File(base, "Android/data").canonicalFile, File(base, "Android/obb").canonicalFile))
            require(canonical != restricted) { "不可创建受系统限制的目录" }
        check(target.mkdir()) { "创建失败，请检查写入权限或存储空间" }
        return target
    }
}
