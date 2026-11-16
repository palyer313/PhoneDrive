package com.phonedrive.android.transfer

import com.phonedrive.android.filemanager.FileProvider
import java.io.IOException

class SourceDeletionException(
    val deletedCount: Int,
    val remainingCount: Int,
    val stagedPaths: List<String>,
    message: String,
    cause: Throwable? = null,
) : IOException(message, cause)

/**
 * Deletes verified move sources in two phases:
 * 1. rename every source to a same-directory staging name;
 * 2. delete staged files.
 *
 * A failure during phase 1 restores everything. A failure during phase 2 restores the
 * current and remaining files and reports exactly how many were already deleted.
 */
internal object TwoPhaseSourceDeleter {

    private data class Staged(val original: String, val staged: String)

    fun delete(
        src: FileProvider,
        sourcePaths: List<String>,
        taskId: String,
        checkControl: () -> Unit,
    ) {
        val staged = mutableListOf<Staged>()
        try {
            sourcePaths.distinct().forEachIndexed { index, source ->
                checkControl()
                val parent = FileProvider.parentOf(source)
                val stagedPath = FileProvider.join(parent, ".phonedrive-delete-$taskId-$index")
                if (src.exists(stagedPath)) {
                    throw IOException("源文件删除暂存路径已存在，未删除任何文件：$stagedPath")
                }
                if (!src.rename(source, stagedPath)) {
                    throw IOException("无法暂存源文件，未删除：$source")
                }
                staged += Staged(source, stagedPath)
            }
        } catch (error: Throwable) {
            val restoreErrors = restore(src, staged.asReversed())
            if (restoreErrors.isNotEmpty()) {
                val kept = staged.map { it.staged }
                throw IOException(
                    "源删除准备失败，且部分文件无法恢复，暂存文件保留于：${kept.joinToString()}；" +
                        restoreErrors.joinToString("；"),
                    error,
                )
            }
            throw error
        }

        staged.forEachIndexed { index, item ->
            try {
                checkControl()
                if (!src.delete(item.staged) || src.exists(item.staged)) {
                    throw IOException("无法删除已暂存源文件：${item.staged}")
                }
            } catch (error: Throwable) {
                val remaining = staged.drop(index)
                val restoreErrors = restore(src, remaining.asReversed())
                throw SourceDeletionException(
                    deletedCount = index,
                    remainingCount = remaining.size,
                    stagedPaths = remaining.map { it.staged },
                    message = buildString {
                        append("目标已验证，但源删除在第 ${index + 1} 项失败；")
                        append("已删除 $index 项，剩余 ${remaining.size} 项")
                        append(if (restoreErrors.isEmpty()) "已恢复原路径。" else "未能全部恢复：${restoreErrors.joinToString("；")}")
                    },
                    cause = error,
                )
            }
        }
    }

    private fun restore(src: FileProvider, items: List<Staged>): List<String> {
        val errors = mutableListOf<String>()
        for (item in items) {
            try {
                if (src.exists(item.original)) {
                    errors += "原路径已被占用：${item.original}"
                    continue
                }
                if (!src.rename(item.staged, item.original)) {
                    errors += "无法恢复：${item.staged} -> ${item.original}"
                }
            } catch (error: Exception) {
                errors += "${item.staged}：${error.message ?: error.javaClass.simpleName}"
            }
        }
        return errors
    }
}
