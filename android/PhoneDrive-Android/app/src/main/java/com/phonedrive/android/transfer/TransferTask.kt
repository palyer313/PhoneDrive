package com.phonedrive.android.transfer

import com.phonedrive.android.filemanager.FileProvider
import org.json.JSONObject
import java.util.UUID

enum class TransferType { COPY, MOVE }

enum class TransferState {
    QUEUED, RUNNING, PAUSED, VERIFYING, DONE, FAILED, CANCELED, SKIPPED;

    val isTerminal: Boolean
        get() = this == DONE || this == FAILED || this == CANCELED || this == SKIPPED
}

enum class ConflictPolicy {
    /** 引擎侧禁止静默覆盖，ASK 等价于自动重命名（UI 必须提前询问用户） */
    ASK, OVERWRITE, SKIP, RENAME, KEEP_BOTH
}

/**
 * 传输任务。srcDeviceId / dstDeviceId 为 null 表示本机共享存储。
 * resumeOffset 为断点续传起点（目标端 .phonedrive-tmp 已写入的字节数）。
 */
data class TransferTask(
    val taskId: String,
    val type: TransferType,
    val srcDeviceId: String?,
    val srcPath: String,
    val dstDeviceId: String?,
    val dstPath: String,
    val size: Long,
    val transferred: Long = 0L,
    val state: TransferState = TransferState.QUEUED,
    val error: String? = null,
    val resumeOffset: Long = 0L,
    val conflictPolicy: ConflictPolicy = ConflictPolicy.ASK,
    val srcIsDirectory: Boolean = false,
    /** 源文件最后修改时间，用于判断断点续传是否仍有效 */
    val srcModifiedAt: Long = 0L,
    val createdAt: Long,
    val updatedAt: Long,
    val speedBps: Long = 0L,
    val etaSeconds: Long = -1L,
) {
    val displayName: String
        get() = FileProvider.nameOf(dstPath)

    val progress: Float
        get() = if (size <= 0L) if (state == TransferState.DONE) 1f else 0f
        else (transferred.toFloat() / size).coerceIn(0f, 1f)

    val isActive: Boolean
        get() = state == TransferState.RUNNING ||
            state == TransferState.QUEUED ||
            state == TransferState.VERIFYING

    fun toJson(): JSONObject = JSONObject().apply {
        put("taskId", taskId)
        put("type", type.name)
        put("srcDeviceId", srcDeviceId ?: "")
        put("srcPath", srcPath)
        put("dstDeviceId", dstDeviceId ?: "")
        put("dstPath", dstPath)
        put("size", size)
        put("transferred", transferred)
        put("state", state.name)
        put("error", error ?: "")
        put("resumeOffset", resumeOffset)
        put("conflictPolicy", conflictPolicy.name)
        put("srcIsDirectory", srcIsDirectory)
        put("srcModifiedAt", srcModifiedAt)
        put("createdAt", createdAt)
        put("updatedAt", updatedAt)
    }

    companion object {
        fun fromJson(obj: JSONObject): TransferTask? {
            val taskId = obj.optString("taskId").ifBlank { return null }
            val srcPath = obj.optString("srcPath")
            val dstPath = obj.optString("dstPath")
            if (srcPath.isBlank() || dstPath.isBlank()) return null
            return TransferTask(
                taskId = taskId,
                type = runCatching { TransferType.valueOf(obj.optString("type", "COPY")) }
                    .getOrDefault(TransferType.COPY),
                srcDeviceId = obj.optString("srcDeviceId").ifBlank { null },
                srcPath = srcPath,
                dstDeviceId = obj.optString("dstDeviceId").ifBlank { null },
                dstPath = dstPath,
                size = obj.optLong("size"),
                transferred = obj.optLong("transferred"),
                state = runCatching { TransferState.valueOf(obj.optString("state", "PAUSED")) }
                    .getOrDefault(TransferState.PAUSED),
                error = obj.optString("error").ifBlank { null },
                resumeOffset = obj.optLong("resumeOffset"),
                conflictPolicy = runCatching {
                    ConflictPolicy.valueOf(obj.optString("conflictPolicy", "ASK"))
                }.getOrDefault(ConflictPolicy.ASK),
                srcIsDirectory = obj.optBoolean("srcIsDirectory", false),
                srcModifiedAt = obj.optLong("srcModifiedAt"),
                createdAt = obj.optLong("createdAt"),
                updatedAt = obj.optLong("updatedAt"),
            )
        }

        fun create(
            type: TransferType,
            srcDeviceId: String?,
            srcPath: String,
            dstDeviceId: String?,
            dstPath: String,
            size: Long,
            conflictPolicy: ConflictPolicy,
            srcModifiedAt: Long = 0L,
        ): TransferTask {
            val now = System.currentTimeMillis()
            return TransferTask(
                taskId = UUID.randomUUID().toString(),
                type = type,
                srcDeviceId = srcDeviceId,
                srcPath = srcPath,
                dstDeviceId = dstDeviceId,
                dstPath = dstPath,
                size = size,
                state = TransferState.QUEUED,
                conflictPolicy = conflictPolicy,
                srcModifiedAt = srcModifiedAt,
                createdAt = now,
                updatedAt = now,
            )
        }
    }
}

/** 入队请求（由 UI 构造） */
data class TransferRequest(
    val type: TransferType,
    val srcDeviceId: String?,
    val srcPath: String,
    val dstDeviceId: String?,
    val dstPath: String,
    val size: Long,
    val conflictPolicy: ConflictPolicy = ConflictPolicy.ASK,
    val srcModifiedAt: Long = 0L,
)
