package com.phonedrive.android.transfer

import android.content.Context
import com.phonedrive.android.remote.DeviceDirectory
import org.json.JSONArray
import java.io.File

/**
 * 传输任务持久化（filesDir/transfers.json），不依赖 Room。
 * 只保留最近 200 条任务，避免文件无限增长。
 */
class TransferStore(context: Context) {

    companion object {
        private const val MAX_TASKS = 200
    }

    private val file = File(context.applicationContext.filesDir, "transfers.json")

    @Synchronized
    fun load(): List<TransferTask> {
        if (!file.exists()) return emptyList()
        return runCatching {
            val array = JSONArray(file.readText())
            val list = buildList {
                for (i in 0 until array.length()) {
                    TransferTask.fromJson(array.getJSONObject(i))?.let { add(it) }
                }
            }
            list.sortedBy { it.createdAt }
        }.getOrDefault(emptyList())
    }

    @Synchronized
    fun save(tasks: List<TransferTask>) {
        val trimmed = tasks.sortedBy { it.createdAt }.takeLast(MAX_TASKS)
        val array = JSONArray()
        trimmed.forEach { array.put(it.toJson()) }
        DeviceDirectory.atomicWrite(file, array.toString())
    }
}
