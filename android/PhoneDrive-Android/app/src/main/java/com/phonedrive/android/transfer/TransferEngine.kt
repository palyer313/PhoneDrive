package com.phonedrive.android.transfer

import android.content.Context
import android.util.Log
import com.phonedrive.android.filemanager.FileProvider
import com.phonedrive.android.filemanager.ProviderHub
import com.phonedrive.android.settings.AppSettings
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import java.io.IOException
import java.security.MessageDigest
import java.util.UUID
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CancellationException

/**
 * 传输引擎（进程内单例）：
 * - 并发度由设置控制（1-8，默认 2）
 * - 256KiB 分块流式传输，不整文件进内存
 * - 目标先写 xxx.phonedrive-tmp，中断后按临时文件大小续传
 * - move 安全序列：复制 → 大小校验 → 提交重命名 → 二次确认 → 才删除源
 * - 任务持久化到 filesDir/transfers.json
 */
class TransferEngine private constructor(private val context: Context) {

    companion object {
        private const val TAG = "TransferEngine"
        const val CHUNK_SIZE = 256 * 1024
        const val TMP_SUFFIX = ".phonedrive-tmp"

        @Volatile
        private var instance: TransferEngine? = null

        fun get(context: Context): TransferEngine =
            instance ?: synchronized(this) {
                instance ?: TransferEngine(context.applicationContext).also { instance = it }
            }
    }

    private enum class Control { RUN, PAUSE, CANCEL }

    private class PauseSignal : Exception("paused")
    private class CancelSignal : Exception("canceled")

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val store = TransferStore(context)

    private val _tasks = MutableStateFlow<List<TransferTask>>(emptyList())
    val tasks: StateFlow<List<TransferTask>> = _tasks.asStateFlow()

    private val destinationLocks = ConcurrentHashMap<String, Mutex>()
    private data class VerifiedFile(val source: String, val target: String, val hash: String)

    private val controls = ConcurrentHashMap<String, Control>()
    private val runningJobs = ConcurrentHashMap<String, Job>()
    private val dispatchChannel = Channel<Unit>(Channel.CONFLATED)

    @Volatile
    private var lastPersistAt = 0L

    init {
        val restored = store.load().map { task ->
            when (task.state) {
                TransferState.QUEUED, TransferState.RUNNING, TransferState.VERIFYING ->
                    task.copy(state = TransferState.PAUSED, speedBps = 0, etaSeconds = -1)
                else -> task
            }
        }
        _tasks.value = restored
        scope.launch {
            for (ignored in dispatchChannel) {
                var started = dispatchOnce()
                while (started) {
                    delay(80)
                    started = dispatchOnce()
                }
            }
        }
    }

    // ---------------------------------------------------------------- 对外 API

    fun enqueue(request: TransferRequest): TransferTask {
        val task = TransferTask.create(
            type = request.type,
            srcDeviceId = request.srcDeviceId,
            srcPath = request.srcPath,
            dstDeviceId = request.dstDeviceId,
            dstPath = request.dstPath,
            size = request.size,
            conflictPolicy = request.conflictPolicy,
            srcModifiedAt = request.srcModifiedAt,
        )
        _tasks.update { trimTasks(it + task) }
        persist(force = true)
        TransferService.start(context)
        requestDispatch()
        return task
    }

    fun enqueueAll(requests: List<TransferRequest>): List<TransferTask> {
        if (requests.isEmpty()) return emptyList()
        val now = System.currentTimeMillis()
        val created = requests.mapIndexed { index, request ->
            TransferTask.create(
                type = request.type,
                srcDeviceId = request.srcDeviceId,
                srcPath = request.srcPath,
                dstDeviceId = request.dstDeviceId,
                dstPath = request.dstPath,
                size = request.size,
                conflictPolicy = request.conflictPolicy,
                srcModifiedAt = request.srcModifiedAt,
            ).copy(createdAt = now + index, updatedAt = now + index)
        }
        _tasks.update { trimTasks(it + created) }
        persist(force = true)
        TransferService.start(context)
        requestDispatch()
        return created
    }

    fun pause(taskId: String) {
        val task = current(taskId) ?: return
        when (task.state) {
            TransferState.QUEUED ->
                replace(task.copy(state = TransferState.PAUSED, speedBps = 0, etaSeconds = -1), true)
            TransferState.RUNNING, TransferState.VERIFYING ->
                controls[taskId] = Control.PAUSE
            else -> Unit
        }
    }

    fun resume(taskId: String) {
        val task = current(taskId) ?: return
        if (task.state != TransferState.PAUSED && task.state != TransferState.FAILED) return
        replace(
            task.copy(state = TransferState.QUEUED, error = null, speedBps = 0, etaSeconds = -1),
            true
        )
        TransferService.start(context)
        requestDispatch()
    }

    fun cancel(taskId: String) {
        val task = current(taskId) ?: return
        if (task.state.isTerminal) return
        if (task.state == TransferState.RUNNING || task.state == TransferState.VERIFYING) {
            controls[taskId] = Control.CANCEL
        } else {
            replace(task.copy(state = TransferState.CANCELED, speedBps = 0, etaSeconds = -1), true)
        }
    }

    fun retry(taskId: String) = resume(taskId)

    fun remove(taskId: String) {
        val task = current(taskId) ?: return
        if (!task.state.isTerminal) return
        _tasks.update { list -> list.filterNot { it.taskId == taskId } }
        persist(force = true)
    }

    fun clearFinished() {
        _tasks.update { list -> list.filter { !it.state.isTerminal } }
        persist(force = true)
    }

    /** 设置页修改并发度后调用，立即重新调度 */
    fun requestDispatch() {
        dispatchChannel.trySend(Unit)
    }

    fun shutdown() {
        scope.cancel()
    }

    // ---------------------------------------------------------------- 调度

    private fun dispatchOnce(): Boolean {
        val concurrency = AppSettings.transferConcurrency(context)
        var active = _tasks.value.count {
            it.state == TransferState.RUNNING || it.state == TransferState.VERIFYING
        }
        if (active >= concurrency) return false
        val queued = _tasks.value.filter { it.state == TransferState.QUEUED }.sortedBy { it.createdAt }
        var startedAny = false
        for (task in queued) {
            if (active >= concurrency) break
            if (runningJobs.containsKey(task.taskId)) continue
            start(task)
            active++
            startedAny = true
        }
        return startedAny
    }

    private fun start(task: TransferTask) {
        controls[task.taskId] = Control.RUN
        replace(
            task.copy(
                state = TransferState.RUNNING,
                error = null,
                speedBps = 0,
                etaSeconds = -1
            )
        )
        val job = scope.launch { execute(task.taskId) }
        runningJobs[task.taskId] = job
        job.invokeOnCompletion {
            runningJobs.remove(task.taskId)
            controls.remove(task.taskId)
            requestDispatch()
        }
    }

    // ---------------------------------------------------------------- 任务执行

    private suspend fun execute(taskId: String) {
        val task = current(taskId) ?: return
        val key = "${task.dstDeviceId}:${task.dstPath}"
        destinationLocks.getOrPut(key) { Mutex() }.withLock { executeLocked(taskId) }
    }

    private suspend fun executeLocked(taskId: String) {
        var task = current(taskId) ?: return
        var dstProvider: FileProvider? = null
        var targetPath: String? = null

        try {
            val src = ProviderHub.providerFor(task.srcDeviceId)
            val dst = ProviderHub.providerFor(task.dstDeviceId)
            dstProvider = dst

            val srcEntry = src.stat(task.srcPath)

            // 源文件发生变化时，已有临时文件不可信，从头重传
            val sourceChanged = task.srcModifiedAt != 0L && srcEntry.modifiedAt != task.srcModifiedAt
            if (sourceChanged && task.transferred > 0) {
                Log.w(TAG, "source changed, restart transfer: ${task.srcPath}")
            }

            val total = if (srcEntry.isDirectory) measureTree(src, task.srcPath, 0) else srcEntry.size
            task = task.copy(
                size = total,
                srcIsDirectory = srcEntry.isDirectory,
                srcModifiedAt = srcEntry.modifiedAt,
                resumeOffset = if (sourceChanged) 0L else task.resumeOffset,
            )
            replace(task)

            // 源与目标完全相同：直接跳过，避免覆盖策略误删源文件
            if (task.srcDeviceId == task.dstDeviceId && task.srcPath == task.dstPath) {
                replace(
                    task.copy(state = TransferState.SKIPPED, speedBps = 0, etaSeconds = 0),
                    true
                )
                return
            }

            // 断点恢复（已传过一部分）时不重走冲突重命名，目标沿用原路径，
            // 否则目录任务会被整树复制到 "dirname (2)" 造成重复副本
            val resuming = task.transferred > 0
            val resolved = if (resuming) task.dstPath
            else resolveTarget(dst, task.dstPath, task.conflictPolicy)
            if (resolved == null) {
                replace(
                    task.copy(state = TransferState.SKIPPED, speedBps = 0, etaSeconds = 0),
                    true
                )
                return
            }
            targetPath = resolved
            if (resolved != task.dstPath) {
                task = task.copy(dstPath = resolved)
                replace(task)
            }

            // 源已变化：旧临时文件内容不可信，删除后从头传
            if (sourceChanged) {
                runCatching { dst.delete(resolved + TMP_SUFFIX) }
            }

            // 同 provider 下把目录放入自身子树会造成无限自递归
            if (srcEntry.isDirectory && task.srcDeviceId == task.dstDeviceId) {
                val srcNorm = task.srcPath.trimEnd('/')
                val dstNorm = resolved.trimEnd('/')
                if (dstNorm.startsWith("$srcNorm/")) {
                    throw IOException("不能把目录复制到其自身内部：$resolved")
                }
            }

            // 同设备 move：直接原子重命名，避免不必要的复制
            if (task.type == TransferType.MOVE && task.srcDeviceId == task.dstDeviceId && !dst.exists(resolved)) {
                checkControl(taskId)
                if (!dst.rename(task.srcPath, resolved)) {
                    throw IOException("重命名失败，源文件已保留")
                }
                if (!dst.exists(resolved)) {
                    throw IOException("移动后目标文件不存在，源文件已保留")
                }
                replace(
                    task.copy(
                        state = TransferState.DONE,
                        transferred = total,
                        resumeOffset = total,
                        speedBps = 0,
                        etaSeconds = 0,
                    ),
                    true
                )
                return
            }

            val tracker = ProgressTracker(this, taskId, total, 0L)
            val verified = mutableListOf<VerifiedFile>()
            val complete = if (srcEntry.isDirectory) {
                copyTree(taskId, src, dst, task.srcPath, resolved, tracker, 0, task.conflictPolicy, resuming, verified)
            } else {
                val hash = copyFileData(taskId, src, task.srcPath, dst, resolved, total, tracker)
                verified.add(VerifiedFile(task.srcPath, resolved, hash))
                true
            }
            checkControl(taskId)
            replace((current(taskId) ?: task).copy(state = TransferState.VERIFYING))
            var warning: String? = if (!complete) "部分文件已跳过；未删除对应源文件" else null
            if (task.type == TransferType.MOVE) {
                // Validate every source and destination before deleting any member of a directory.
                if (!complete) warning = "目录中有跳过的文件，已保留整个源目录"
                else {
                    if (srcEntry.isDirectory) verifyTreeMembership(src, task.srcPath, verified, 0)
                    for (file in verified) {
                        checkControl(taskId)
                        if (hashFile(src, file.source, taskId) != file.hash || hashFile(dst, file.target, taskId) != file.hash)
                            throw IOException("移动提交后文件发生变化，源文件已保留：${file.source}")
                    }
                    for (file in verified) {
                        checkControl(taskId)
                        if (hashFile(src, file.source, taskId) != file.hash)
                            throw IOException("源文件已变化，未删除：${file.source}")
                        if (!src.delete(file.source)) throw IOException("目标已验证，但源文件删除失败：${file.source}")
                    }
                    if (srcEntry.isDirectory) removeEmptyTree(src, task.srcPath, 0)
                }
            }
            replace((current(taskId) ?: task).copy(
                state = TransferState.DONE,
                transferred = if (complete) total else tracker.transferred,
                resumeOffset = if (complete) total else tracker.transferred,
                speedBps = 0, etaSeconds = 0, error = warning,
            ), true)
        } catch (pause: PauseSignal) {
            val snapshot = current(taskId)
            if (snapshot != null) {
                replace(
                    snapshot.copy(
                        state = TransferState.PAUSED,
                        resumeOffset = snapshot.transferred,
                        speedBps = 0,
                        etaSeconds = -1,
                    ),
                    true
                )
            }
        } catch (cancel: CancelSignal) {
            cleanupTemp(dstProvider, targetPath, taskId)
            val snapshot = current(taskId)
            if (snapshot != null) {
                replace(
                    snapshot.copy(state = TransferState.CANCELED, speedBps = 0, etaSeconds = -1),
                    true
                )
            }
        } catch (cancellation: CancellationException) {
            throw cancellation
        } catch (e: Exception) {
            Log.e(TAG, "task $taskId failed", e)
            val snapshot = current(taskId)
            if (snapshot != null) {
                replace(
                    snapshot.copy(
                        state = TransferState.FAILED,
                        error = e.message ?: e.javaClass.simpleName,
                        speedBps = 0,
                        etaSeconds = -1,
                    ),
                    true
                )
            }
        } finally {
            snapshotProgress(taskId)
        }
    }

    private fun cleanupTemp(dst: FileProvider?, targetPath: String?, taskId: String) {
        if (dst == null || targetPath == null) return
        runCatching { dst.delete(targetPath + "." + taskId + TMP_SUFFIX) }
    }

    private fun hashFile(provider: FileProvider, path: String, taskId: String, limit: Long? = null): String {
        val hash = MessageDigest.getInstance("SHA-256")
        var remaining = limit ?: Long.MAX_VALUE
        provider.openRead(path).use { input ->
            val buffer = ByteArray(CHUNK_SIZE)
            while (remaining > 0) {
                checkControl(taskId)
                val read = input.read(buffer, 0, minOf(buffer.size.toLong(), remaining).toInt())
                if (read <= 0) break
                hash.update(buffer, 0, read); remaining -= read
            }
        }
        if (limit != null && remaining != 0L) throw IOException("续传前缀读取不完整")
        return hash.digest().joinToString("") { "%02x".format(it.toInt() and 255) }
    }

    private suspend fun copyFileData(
        taskId: String, src: FileProvider, srcPath: String, dst: FileProvider,
        dstPath: String, total: Long, tracker: ProgressTracker,
    ): String {
        val tmpPath = "$dstPath.$taskId$TMP_SUFFIX"
        val expected = hashFile(src, srcPath, taskId)
        val existing = runCatching { dst.stat(tmpPath).size }.getOrDefault(0L)
        var offset = if (existing in 0..total) existing else 0L
        if (existing > total || (offset > 0 && hashFile(src, srcPath, taskId, offset) != hashFile(dst, tmpPath, taskId, offset))) {
            if (!dst.delete(tmpPath)) throw IOException("旧临时文件无法重置")
            offset = 0
        }
        tracker.add(offset, silent = true)
        if (offset < total || !dst.exists(tmpPath)) {
            src.openRead(srcPath, offset).use { input ->
                dst.openWrite(tmpPath, append = offset > 0).use { output ->
                    val buffer = ByteArray(CHUNK_SIZE)
                    while (true) {
                        checkControl(taskId)
                        val count = input.read(buffer)
                        if (count <= 0) break
                        output.write(buffer, 0, count); tracker.add(count.toLong())
                    }
                    output.flush()
                }
            }
        }
        if (dst.stat(tmpPath).size != total || hashFile(dst, tmpPath, taskId) != expected)
            throw IOException("目标临时文件 SHA-256 校验失败，源文件已保留")
        if (hashFile(src, srcPath, taskId) != expected) throw IOException("传输期间源文件发生变化")
        checkControl(taskId)
        val backup = "$dstPath.phonedrive-backup-${UUID.randomUUID()}"
        var backedUp = false
        var committed = false
        try {
            if (dst.exists(dstPath)) {
                val policy = current(taskId)?.conflictPolicy
                if (policy != ConflictPolicy.OVERWRITE) throw IOException("传输期间目标已出现，未覆盖")
                if (!dst.rename(dstPath, backup)) throw IOException("无法保护旧目标，未覆盖")
                backedUp = true
            }
            if (!dst.rename(tmpPath, dstPath)) throw IOException("提交失败")
            committed = true
            if (hashFile(dst, dstPath, taskId) != expected) throw IOException("提交后目标 SHA-256 不一致")
        } catch (error: Exception) {
            try {
                if (committed && !dst.delete(dstPath)) throw IOException("无法移除失败的提交")
                if (backedUp && !dst.rename(backup, dstPath)) throw IOException("旧目标自动恢复失败")
            } catch (restore: Exception) {
                throw IOException("提交失败，旧目标备份保留于 $backup：${restore.message}", error)
            }
            throw error
        }
        if (backedUp) runCatching { dst.delete(backup) }
        return expected
    }

    private suspend fun copyTree(
        taskId: String, src: FileProvider, dst: FileProvider, srcDir: String, dstDir: String,
        tracker: ProgressTracker, depth: Int, policy: ConflictPolicy, resuming: Boolean,
        verified: MutableList<VerifiedFile>,
    ): Boolean {
        if (depth > 64) throw IOException("目录层级过深：$srcDir")
        checkControl(taskId)
        if (!dst.exists(dstDir) && !dst.mkdir(dstDir)) throw IOException("创建目录失败：$dstDir")
        var complete = true
        for (child in src.list(srcDir)) {
            checkControl(taskId)
            if (child.isSymlink) throw IOException("不自动跟随符号链接：${child.path}")
            var target = FileProvider.join(dstDir, child.name)
            if (child.isDirectory) {
                if (!copyTree(taskId, src, dst, child.path, target, tracker, depth + 1, policy, resuming, verified)) complete = false
                continue
            }
            if (dst.exists(target)) {
                val matches = runCatching { dst.stat(target).size == child.size &&
                    hashFile(src, child.path, taskId) == hashFile(dst, target, taskId) }.getOrDefault(false)
                if (matches && (resuming || policy == ConflictPolicy.SKIP)) {
                    verified.add(VerifiedFile(child.path, target, hashFile(src, child.path, taskId)))
                    tracker.add(child.size, silent = true); continue
                }
                if (policy == ConflictPolicy.SKIP) { complete = false; continue }
                if (policy != ConflictPolicy.OVERWRITE) target = uniquePath(dst, target)
            }
            val hash = copyFileData(taskId, src, child.path, dst, target, child.size, tracker)
            verified.add(VerifiedFile(child.path, target, hash))
        }
        return complete
    }

    private fun verifyTreeMembership(src: FileProvider, path: String, files: List<VerifiedFile>, depth: Int) {
        if (depth > 64) throw IOException("目录层级过深")
        for (child in src.list(path)) {
            if (child.isSymlink) throw IOException("源目录包含符号链接，已保留")
            if (child.isDirectory) verifyTreeMembership(src, child.path, files, depth + 1)
            else if (files.none { it.source == child.path }) throw IOException("源目录出现新文件，已保留：${child.path}")
        }
    }

    private fun removeEmptyTree(src: FileProvider, path: String, depth: Int) {
        if (depth > 64) return
        for (child in src.list(path)) if (child.isDirectory && !child.isSymlink) removeEmptyTree(src, child.path, depth + 1)
        if (src.list(path).isEmpty()) src.deleteEmptyDirectory(path)
    }

    private suspend fun measureTree(src: FileProvider, path: String, depth: Int): Long {
        if (depth > 64) throw IOException("目录层级过深")
        val children = src.list(path)
        var total = 0L
        for (child in children) {
            if (child.isDirectory) {
                if (!child.isSymlink) total += measureTree(src, child.path, depth + 1)
            } else {
                total += child.size
            }
        }
        return total
    }

    // ---------------------------------------------------------------- 冲突 / 工具

    /** 返回最终目标路径；null 表示按策略跳过 */
    private fun resolveTarget(
        dst: FileProvider,
        path: String,
        policy: ConflictPolicy,
    ): String? {
        if (!dst.exists(path)) return path
        return when (policy) {
            ConflictPolicy.SKIP -> null
            ConflictPolicy.OVERWRITE -> path
            else -> uniquePath(dst, path)
        }
    }

    private fun uniquePath(dst: FileProvider, path: String): String {
        val dir = FileProvider.parentOf(path)
        val name = FileProvider.nameOf(path)
        val dot = name.lastIndexOf('.')
        val stem = if (dot > 0) name.substring(0, dot) else name
        val ext = if (dot > 0) name.substring(dot) else ""
        for (index in 2..999) {
            val candidate = FileProvider.join(dir, "$stem ($index)$ext")
            if (!dst.exists(candidate)) return candidate
        }
        throw IOException("同名文件过多，无法自动重命名：$name")
    }

    private fun checkControl(taskId: String) {
        when (controls[taskId]) {
            Control.PAUSE -> throw PauseSignal()
            Control.CANCEL -> throw CancelSignal()
            else -> Unit
        }
    }

    /** 内存中最多保留 200 条任务，优先淘汰最早的已结束任务 */
    private fun trimTasks(list: List<TransferTask>): List<TransferTask> {
        if (list.size <= 200) return list
        val removable = list.filter { it.state.isTerminal }
            .sortedBy { it.createdAt }
            .take(list.size - 200)
        return list - removable.toSet()
    }

    private fun current(taskId: String): TransferTask? =
        _tasks.value.firstOrNull { it.taskId == taskId }

    private fun replace(task: TransferTask, forcePersist: Boolean = false) {
        _tasks.update { list -> list.map { if (it.taskId == task.taskId) task else it } }
        persist(forcePersist)
    }

    internal fun updateProgress(taskId: String, transferred: Long, speedBps: Long, etaSeconds: Long) {
        val now = System.currentTimeMillis()
        _tasks.update { list ->
            list.map {
                if (it.taskId == taskId) {
                    it.copy(
                        transferred = transferred,
                        speedBps = speedBps,
                        etaSeconds = etaSeconds,
                        updatedAt = now,
                    )
                } else {
                    it
                }
            }
        }
        persist(force = false)
    }

    private fun snapshotProgress(taskId: String) {
        persist(force = true)
    }

    private fun persist(force: Boolean) {
        val now = System.currentTimeMillis()
        if (!force && now - lastPersistAt < 1200L) return
        lastPersistAt = now
        val snapshot = _tasks.value
        runCatching { store.save(snapshot) }
            .onFailure { Log.w(TAG, "persist failed", it) }
    }

    /**
     * 进度跟踪：节流更新 StateFlow；按 1 秒窗口估算速度与剩余时间。
     */
    private class ProgressTracker(
        private val engine: TransferEngine,
        private val taskId: String,
        private val total: Long,
        start: Long,
    ) {
        var transferred: Long = start
            private set

        private var windowStart = System.currentTimeMillis()
        private var windowBytes = start
        private var lastUpdate = 0L

        fun add(delta: Long, silent: Boolean = false) {
            transferred += delta
            val now = System.currentTimeMillis()
            if (now - windowStart >= 1000L) {
                val elapsed = (now - windowStart).coerceAtLeast(1L)
                val speed = (transferred - windowBytes) * 1000L / elapsed
                val eta = if (speed > 0 && total > transferred) (total - transferred) / speed else 0L
                windowStart = now
                windowBytes = transferred
                lastUpdate = now
                engine.updateProgress(taskId, transferred, speed, eta)
            } else if (!silent && now - lastUpdate >= 200L) {
                lastUpdate = now
                engine.updateProgress(taskId, transferred, 0L, -1L)
            }
        }
    }
}
