package com.phonedrive.android.remote

import android.content.Context
import com.phonedrive.android.filemanager.ProviderHub
import com.phonedrive.android.settings.AppSettings
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.util.concurrent.ConcurrentHashMap

/**
 * 已配对设备的自动重连（协议 §6）。
 *
 * - 观察 mDNS 发现结果，按 deviceid 关联配对记录；
 * - 上线时比对 mDNS TXT 的 fp 与配对记录中的 hostFp，一致才自动连接，
 *   不一致标记「身份冲突」并永久停止自动连接（需用户重新配对）；
 * - 连接失败按 1s/2s/4s/8s/16s 指数退避（上限 30s）持续重试；
 * - 认证失败不重试；
 * - IP/端口变化时更新配对记录；
 * - 设备下线时关闭会话并把状态置为离线。
 */
class ConnectionSupervisor(
    private val context: Context,
    private val discovery: PeerDiscovery,
) {

    companion object {
        private const val BASE_BACKOFF_MS = 1_000L
        private const val MAX_BACKOFF_MS = 30_000L
        private const val RECONCILE_INTERVAL_MS = 3_000L
        private const val CONNECTED_POLL_MS = 2_000L
        private const val CONFLICT_MESSAGE =
            "身份冲突：发现设备的 Host Key 指纹与配对记录不一致，已停止自动连接，请重新配对"
        private const val MISSING_FINGERPRINT_MESSAGE =
            "无法验证身份：发现设备未广播 Host Key 指纹，已停止自动连接，请重新配对"
    }

    /** 单台设备的连接状态，UI 据此展示在线/离线/重试/冲突 */
    data class LinkState(
        val online: Boolean = false,
        val connected: Boolean = false,
        val conflict: Boolean = false,
        val authFailed: Boolean = false,
        val nextRetryInSeconds: Int = -1,
        val lastError: String? = null,
    )

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val directory = DeviceDirectory.getInstance(context)

    private val _links = MutableStateFlow<Map<String, LinkState>>(emptyMap())
    val links: StateFlow<Map<String, LinkState>> = _links.asStateFlow()

    private val peers = ConcurrentHashMap<String, DiscoveredPeer>()
    private val linkJobs = ConcurrentHashMap<String, Job>()
    /** 用户手动断开过的设备，在设备下线/用户手动连接前不再自动连接 */
    private val suppressed = ConcurrentHashMap.newKeySet<String>()
    private val reconcileLock = Mutex()

    private var peersJob: Job? = null
    private var devicesJob: Job? = null
    private var pollJob: Job? = null

    @Volatile
    private var running = false

    fun start() {
        if (running) return
        running = true
        peers.clear()
        discovery.peers.value.forEach { peers[it.deviceId] = it }

        peersJob = scope.launch {
            discovery.peers.collect { list ->
                peers.clear()
                list.forEach { peers[it.deviceId] = it }
                reconcile()
            }
        }
        devicesJob = scope.launch {
            directory.devices.collect { reconcile() }
        }
        pollJob = scope.launch {
            while (isActive) {
                delay(RECONCILE_INTERVAL_MS)
                reconcile()
            }
        }
        scope.launch { reconcile() }
    }

    /** 设置页切换自动重连开关后调用，立即重新评估 */
    fun refresh() {
        if (!running) return
        scope.launch { reconcile() }
    }

    fun stop() {
        running = false
        peersJob?.cancel()
        devicesJob?.cancel()
        pollJob?.cancel()
        peersJob = null
        devicesJob = null
        pollJob = null
        linkJobs.values.forEach { runCatching { it.cancel() } }
        linkJobs.clear()
        _links.value = emptyMap()
    }

    fun linkState(deviceId: String): LinkState? = _links.value[deviceId]

    /** 用户手动断开：暂停该设备的自动重连，直到设备下线或用户手动连接 */
    fun disconnectDevice(deviceId: String) {
        suppressed.add(deviceId)
        linkJobs.remove(deviceId)?.cancel()
        ProviderHub.disconnect(deviceId)
        setState(deviceId) { it.copy(connected = false, nextRetryInSeconds = -1) }
    }

    /** 用户手动连接前调用，恢复该设备的自动重连 */
    fun clearSuppression(deviceId: String) {
        if (suppressed.remove(deviceId)) refresh()
    }

    // ---------------------------------------------------------------- 协调

    private suspend fun reconcile() {
        if (!running) return
        reconcileLock.withLock {
            if (!running) return
            val paired = directory.list()
            val knownIds = paired.map { it.deviceId }.toSet()

            linkJobs.keys.filterNot { it in knownIds }.forEach { id ->
                linkJobs.remove(id)?.cancel()
            }
            suppressed.removeAll { it !in knownIds }
            _links.update { map -> map.filterKeys { it in knownIds } }

            val autoEnabled = AppSettings.isAutoReconnectEnabled(context)

            for (device in paired.filter { it.type != "windows" }) {
                val peer = peers[device.deviceId]
                if (peer == null) {
                    linkJobs.remove(device.deviceId)?.cancel()
                    suppressed.remove(device.deviceId)
                    if (ProviderHub.isConnected(device.deviceId)) {
                        ProviderHub.disconnect(device.deviceId)
                    }
                    setState(device.deviceId) {
                        it.copy(online = false, connected = false, nextRetryInSeconds = -1)
                    }
                    continue
                }

                val fingerprintMissing = peer.fingerprint.isBlank()
                val fingerprintMismatch = !fingerprintMissing &&
                    peer.fingerprint != device.hostKeyFingerprint
                if (fingerprintMismatch || fingerprintMissing) {
                    linkJobs.remove(device.deviceId)?.cancel()
                    if (ProviderHub.isConnected(device.deviceId)) {
                        ProviderHub.disconnect(device.deviceId)
                    }
                    _links.update {
                        it + (device.deviceId to LinkState(
                            online = true,
                            conflict = true,
                            lastError = if (fingerprintMissing) {
                                MISSING_FINGERPRINT_MESSAGE
                            } else {
                                CONFLICT_MESSAGE
                            },
                        ))
                    }
                    continue
                }

                // 用最新发现的地址/端口覆盖配对记录
                if (peer.host != device.lastKnownIp || peer.port != device.lastKnownPort) {
                    directory.touch(device.deviceId, peer.host, peer.port, peer.lastSeenAt)
                }

                setState(device.deviceId) {
                    it.copy(online = true, conflict = false)
                }
                if (ProviderHub.isConnected(device.deviceId)) {
                    setState(device.deviceId) {
                        it.copy(online = true, connected = true, nextRetryInSeconds = -1)
                    }
                } else if (autoEnabled && device.deviceId !in suppressed &&
                    linkJobs[device.deviceId]?.isActive != true
                ) {
                    linkJobs[device.deviceId] = scope.launch { runLinkLoop(device.deviceId) }
                }
            }
        }
    }

    // ---------------------------------------------------------------- 连接循环

    private suspend fun runLinkLoop(deviceId: String) {
        var attempt = 0
        try {
            while (currentCoroutineContext().isActive && running) {
                if (!AppSettings.isAutoReconnectEnabled(context)) break
                val device = directory.find(deviceId) ?: break
                val peer = peers[deviceId] ?: break
                if (peer.fingerprint.isBlank() ||
                    peer.fingerprint != device.hostKeyFingerprint
                ) {
                    _links.update {
                        it + (deviceId to LinkState(
                            online = true,
                            conflict = true,
                            lastError = if (peer.fingerprint.isBlank()) {
                                MISSING_FINGERPRINT_MESSAGE
                            } else {
                                CONFLICT_MESSAGE
                            },
                        ))
                    }
                    break
                }

                if (ProviderHub.isConnected(deviceId)) {
                    setState(deviceId) {
                        it.copy(online = true, connected = true, nextRetryInSeconds = -1, lastError = null)
                    }
                    while (currentCoroutineContext().isActive && running &&
                        ProviderHub.isConnected(deviceId) && peers[deviceId] != null
                    ) {
                        delay(CONNECTED_POLL_MS)
                    }
                    if (peers[deviceId] == null) break
                    if (!running) break
                    setState(deviceId) { it.copy(connected = false) }
                }

                try {
                    ProviderHub.providerFor(deviceId)
                    attempt = 0
                    setState(deviceId) {
                        LinkState(online = true, connected = true)
                    }
                } catch (cancellation: CancellationException) {
                    throw cancellation
                } catch (e: Exception) {
                    if (isAuthFailure(e)) {
                        setState(deviceId) {
                            LinkState(
                                online = true,
                                authFailed = true,
                                lastError = "认证失败：对方拒绝本机公钥，已停止自动重连；请重新配对或检查授权公钥",
                            )
                        }
                        break
                    }
                    attempt += 1
                    val delayMillis = backoffMillis(attempt)
                    val error = e.message ?: e.javaClass.simpleName
                    val seconds = (delayMillis / 1000).toInt()
                    for (remaining in seconds downTo 1) {
                        if (!currentCoroutineContext().isActive || !running) return
                        if (peers[deviceId] == null) return
                        if (!AppSettings.isAutoReconnectEnabled(context)) return
                        setState(deviceId) {
                            it.copy(
                                online = true,
                                connected = false,
                                nextRetryInSeconds = remaining,
                                lastError = error,
                            )
                        }
                        delay(1_000)
                    }
                }
            }
        } finally {
            linkJobs.remove(deviceId)
            setState(deviceId) { it.copy(nextRetryInSeconds = -1) }
        }
    }

    private fun backoffMillis(attempt: Int): Long {
        if (attempt <= 1) return BASE_BACKOFF_MS
        var delay = BASE_BACKOFF_MS
        repeat(attempt - 1) {
            delay *= 2
        }
        return minOf(delay, MAX_BACKOFF_MS)
    }

    private fun isAuthFailure(error: Throwable): Boolean {
        var current: Throwable? = error
        var depth = 0
        while (current != null && depth < 8) {
            val name = current.javaClass.name
            val message = current.message.orEmpty()
            val auth = name.contains("AuthException", ignoreCase = true) ||
                message.contains("Exhausted available authentication", ignoreCase = true) ||
                message.contains("authentication", ignoreCase = true) ||
                message.contains("USERAUTH", ignoreCase = true) ||
                message.contains("publickey", ignoreCase = true)
            if (auth) return true
            current = current.cause
            depth++
        }
        return false
    }

    private fun setState(deviceId: String, transform: (LinkState) -> LinkState) {
        _links.update { map -> map + (deviceId to transform(map[deviceId] ?: LinkState())) }
    }
}
