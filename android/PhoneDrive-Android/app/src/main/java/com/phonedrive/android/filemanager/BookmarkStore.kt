package com.phonedrive.android.filemanager

import android.content.Context
import com.phonedrive.android.remote.DeviceDirectory
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/** 收藏的目录/文件位置 */
data class Bookmark(
    val deviceId: String?,
    val path: String,
    val name: String,
    val isDirectory: Boolean,
    val addedAt: Long,
) {
    val location: StorageLocation get() = StorageLocation(deviceId, path)
}

/** 最近访问目录 */
data class RecentLocation(
    val deviceId: String?,
    val path: String,
    val name: String,
    val visitedAt: Long,
) {
    val location: StorageLocation get() = StorageLocation(deviceId, path)
}

/**
 * 收藏与历史（filesDir/bookmarks.json）。
 * 收藏仅保存逻辑定位（设备 + 路径），不缓存文件内容。
 */
class BookmarkStore private constructor(context: Context) {

    companion object {
        private const val MAX_HISTORY = 50
        private const val MAX_FAVORITES = 200

        @Volatile
        private var instance: BookmarkStore? = null

        fun getInstance(context: Context): BookmarkStore =
            instance ?: synchronized(this) {
                instance ?: BookmarkStore(context).also { instance = it }
            }
    }

    private val file = File(context.applicationContext.filesDir, "bookmarks.json")

    private val _favorites = MutableStateFlow<List<Bookmark>>(emptyList())
    val favorites: StateFlow<List<Bookmark>> = _favorites.asStateFlow()

    private val _history = MutableStateFlow<List<RecentLocation>>(emptyList())
    val history: StateFlow<List<RecentLocation>> = _history.asStateFlow()

    init {
        synchronized(this) { load() }
    }

    @Synchronized
    fun addFavorite(deviceId: String?, path: String, name: String, isDirectory: Boolean) {
        if (isFavorite(deviceId, path)) return
        val list = (_favorites.value + Bookmark(
            deviceId = deviceId,
            path = path,
            name = name,
            isDirectory = isDirectory,
            addedAt = System.currentTimeMillis(),
        )).takeLast(MAX_FAVORITES)
        _favorites.value = list
        persist()
    }

    @Synchronized
    fun removeFavorite(deviceId: String?, path: String) {
        _favorites.value = _favorites.value.filterNot { it.deviceId == deviceId && it.path == path }
        persist()
    }

    fun isFavorite(deviceId: String?, path: String): Boolean =
        _favorites.value.any { it.deviceId == deviceId && it.path == path }

    @Synchronized
    fun recordVisit(deviceId: String?, path: String, name: String) {
        val entry = RecentLocation(deviceId, path, name, System.currentTimeMillis())
        val list = (listOf(entry) + _history.value.filterNot { it.deviceId == deviceId && it.path == path })
            .take(MAX_HISTORY)
        _history.value = list
        persist()
    }

    @Synchronized
    fun removeHistory(deviceId: String?, path: String) {
        _history.value = _history.value.filterNot { it.deviceId == deviceId && it.path == path }
        persist()
    }

    @Synchronized
    fun clearHistory() {
        _history.value = emptyList()
        persist()
    }

    @Synchronized
    private fun load() {
        if (!file.exists()) return
        runCatching {
            val root = JSONObject(file.readText())
            val favorites = root.optJSONArray("favorites") ?: JSONArray()
            _favorites.value = buildList {
                for (i in 0 until favorites.length()) {
                    val o = favorites.getJSONObject(i)
                    add(
                        Bookmark(
                            deviceId = o.optString("deviceId").ifBlank { null },
                            path = o.optString("path", "/"),
                            name = o.optString("name", "/"),
                            isDirectory = o.optBoolean("isDirectory", true),
                            addedAt = o.optLong("addedAt"),
                        )
                    )
                }
            }
            val history = root.optJSONArray("history") ?: JSONArray()
            _history.value = buildList {
                for (i in 0 until history.length()) {
                    val o = history.getJSONObject(i)
                    add(
                        RecentLocation(
                            deviceId = o.optString("deviceId").ifBlank { null },
                            path = o.optString("path", "/"),
                            name = o.optString("name", "/"),
                            visitedAt = o.optLong("visitedAt"),
                        )
                    )
                }
            }
        }
    }

    private fun persist() {
        val root = JSONObject()
        val favorites = JSONArray()
        _favorites.value.forEach { f ->
            favorites.put(JSONObject().apply {
                put("deviceId", f.deviceId ?: "")
                put("path", f.path)
                put("name", f.name)
                put("isDirectory", f.isDirectory)
                put("addedAt", f.addedAt)
            })
        }
        val history = JSONArray()
        _history.value.forEach { h ->
            history.put(JSONObject().apply {
                put("deviceId", h.deviceId ?: "")
                put("path", h.path)
                put("name", h.name)
                put("visitedAt", h.visitedAt)
            })
        }
        root.put("favorites", favorites)
        root.put("history", history)
        DeviceDirectory.atomicWrite(file, root.toString())
    }
}
