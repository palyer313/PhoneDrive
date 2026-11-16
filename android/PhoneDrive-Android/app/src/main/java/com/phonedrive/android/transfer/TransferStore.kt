package com.phonedrive.android.transfer

import android.content.Context
import com.phonedrive.android.AppDiagnostics
import com.phonedrive.android.remote.DeviceDirectory
import org.json.JSONArray
import java.io.File
import java.io.IOException

/**
 * 传输任务持久化（filesDir/transfers.json），不依赖 Room。
 * 只保留最近 200 条任务，避免文件无限增长。
 */
class TransferStore(private val context: Context) {

    companion object {
        private const val MAX_TASKS = 200
    }

    private val file = File(context.applicationContext.filesDir, "transfers.json")

    @Synchronized
    fun load(): List<TransferTask> {
        if (!file.exists()) return emptyList()
        return try {
            val array = JSONArray(file.readText())
            val list = buildList {
                for (i in 0 until array.length()) {
                    TransferTask.fromJson(array.getJSONObject(i))?.let { add(it) }
                }
            }
            list.sortedBy { it.createdAt }
        } catch (error: Exception) {
            val preserved = File(file.parentFile, "transfers.json.corrupt-${System.currentTimeMillis()}")
            runCatching { file.copyTo(preserved, overwrite = false) }
            AppDiagnostics.record(
                context,
                "transfer-store",
                IOException("transfers.json 损坏，已保留到 ${preserved.name}", error),
            )
            emptyList()
        }
    }

    @Synchronized
    fun save(tasks: List<TransferTask>) {
        val trimmed = tasks.sortedBy { it.createdAt }.takeLast(MAX_TASKS)
        val array = JSONArray()
        trimmed.forEach { array.put(it.toJson()) }
        DeviceDirectory.atomicWrite(file, array.toString())
    }
}
