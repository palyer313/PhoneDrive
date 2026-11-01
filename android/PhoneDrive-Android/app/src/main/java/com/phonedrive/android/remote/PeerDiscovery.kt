package com.phonedrive.android.remote

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import android.os.Handler
import android.os.Looper
import android.util.Log
import com.phonedrive.android.discovery.NsdAdvertiser
import com.phonedrive.android.security.DeviceIdentity
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.Closeable

/**
 * 局域网对端发现（DNS-SD / mDNS）：_phonedrive._tcp
 *
 * 只负责"发现"，不代表已配对。未配对设备仅在设备页展示，
 * 配对前不允许发起 SFTP 连接（连接层还会做 Host Key 指纹校验）。
 */
data class DiscoveredPeer(
    val deviceId: String,
    val name: String,
    val type: String,
    val host: String,
    val port: Int,
    val fingerprint: String,
    val serviceName: String,
    val lastSeenAt: Long,
)

class PeerDiscovery(private val context: Context) : Closeable {

    companion object {
        private const val TAG = "PeerDiscovery"
        private const val STALE_MS = 30_000L
        private const val CLEAN_INTERVAL_MS = 10_000L
    }

    private val nsdManager: NsdManager =
        context.getSystemService(Context.NSD_SERVICE) as NsdManager

    private val mainHandler = Handler(Looper.getMainLooper())
    private val selfDeviceId: String = DeviceIdentity.getDeviceId(context)

    private val _peers = MutableStateFlow<List<DiscoveredPeer>>(emptyList())
    val peers: StateFlow<List<DiscoveredPeer>> = _peers.asStateFlow()

    private val lock = Any()
    private val resolveQueue = ArrayDeque<NsdServiceInfo>()
    private var resolving = false

    /** serviceName -> deviceId，用于 onServiceLost 反查 */
    private val serviceToDevice = mutableMapOf<String, String>()

    private var discoveryListener: NsdManager.DiscoveryListener? = null
    private var multicastLock: WifiManager.MulticastLock? = null
    private var running = false

    private val cleanupTask = object : Runnable {
        override fun run() {
            val now = System.currentTimeMillis()
            // Do not expire event-based NSD discoveries on an invented 30-second heartbeat.
            if (running) mainHandler.postDelayed(this, CLEAN_INTERVAL_MS)
        }
    }

    fun start() {
        if (running) return
        running = true
        acquireMulticastLock()
        val listener = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) {
                Log.i(TAG, "discovery started: $serviceType")
            }

            override fun onServiceFound(serviceInfo: NsdServiceInfo) {
                if (serviceInfo.serviceType.contains(NsdAdvertiser.SERVICE_TYPE.trimEnd('.'))) {
                    enqueueResolve(serviceInfo)
                }
            }

            override fun onServiceLost(serviceInfo: NsdServiceInfo) {
                synchronized(lock) {
                    val deviceId = serviceToDevice.remove(serviceInfo.serviceName)
                    if (deviceId != null) {
                        _peers.value = _peers.value.filterNot { it.deviceId == deviceId }
                    }
                }
            }

            override fun onDiscoveryStopped(serviceType: String) {
                Log.i(TAG, "discovery stopped: $serviceType")
            }

            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                Log.e(TAG, "start discovery failed: $errorCode")
                stop()
            }

            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {
                Log.e(TAG, "stop discovery failed: $errorCode")
            }
        }
        discoveryListener = listener
        runCatching {
            nsdManager.discoverServices(
                NsdAdvertiser.SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, listener
            )
        }.onFailure {
            Log.e(TAG, "discoverServices failed", it)
            running = false
        }
        mainHandler.postDelayed(cleanupTask, CLEAN_INTERVAL_MS)
    }

    fun stop() {
        if (!running) return
        running = false
        mainHandler.removeCallbacks(cleanupTask)
        discoveryListener?.let { runCatching { nsdManager.stopServiceDiscovery(it) } }
        discoveryListener = null
        synchronized(lock) {
            resolveQueue.clear()
            resolving = false
        }
        releaseMulticastLock()
        _peers.value = emptyList()
    }

    override fun close() = stop()

    private fun acquireMulticastLock() {
        val wifi = context.applicationContext
            .getSystemService(Context.WIFI_SERVICE) as WifiManager
        multicastLock = wifi.createMulticastLock("phonedrive_discovery").apply {
            setReferenceCounted(false)
            acquire()
        }
    }

    private fun releaseMulticastLock() {
        multicastLock?.let { if (it.isHeld) it.release() }
        multicastLock = null
    }

    private fun enqueueResolve(info: NsdServiceInfo) {
        synchronized(lock) {
            // 同一服务重复入队时去重
            if (resolveQueue.none { it.serviceName == info.serviceName }) {
                resolveQueue.add(info)
            }
            if (resolving) return
            resolving = true
        }
        resolveNext()
    }

    private fun resolveNext() {
        val info = synchronized(lock) { resolveQueue.removeFirstOrNull() }
        if (info == null) {
            synchronized(lock) { resolving = false }
            return
        }
        runCatching {
            nsdManager.resolveService(info, object : NsdManager.ResolveListener {
                override fun onResolveFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {
                    Log.w(TAG, "resolve failed: ${serviceInfo.serviceName} code=$errorCode")
                    resolveNext()
                }

                override fun onServiceResolved(serviceInfo: NsdServiceInfo) {
                    handleResolved(serviceInfo)
                    resolveNext()
                }
            })
        }.onFailure {
            Log.e(TAG, "resolveService error", it)
            resolveNext()
        }
    }

    private fun handleResolved(info: NsdServiceInfo) {
        if (!running) return
        val attrs = info.attributes
        val deviceId = attrs.string("deviceid") ?: return
        if (deviceId == selfDeviceId) return
        val host = info.host?.hostAddress ?: return
        val name = attrs.string("name") ?: info.serviceName
        val type = attrs.string("type") ?: "phone"
        val port = attrs.string("sftpport")?.toIntOrNull() ?: info.port
        val fingerprint = attrs.string("fp") ?: ""
        val peer = DiscoveredPeer(
            deviceId = deviceId,
            name = name,
            type = type,
            host = host,
            port = port,
            fingerprint = fingerprint,
            serviceName = info.serviceName,
            lastSeenAt = System.currentTimeMillis(),
        )
        serviceToDevice[info.serviceName] = deviceId
        _peers.value = _peers.value.filterNot { it.deviceId == deviceId } + peer

        // 已配对设备自动刷新最近已知地址
        val directory = DeviceDirectory.getInstance(context)
        val paired = directory.find(deviceId)
        if (paired != null && paired.hostKeyFingerprint == fingerprint &&
            (paired.lastKnownIp != host || paired.lastKnownPort != port)
        ) {
            // 仅在地址变化时落盘，避免 mDNS 重复通告造成频繁写文件
            directory.touch(deviceId, host, port, peer.lastSeenAt)
        }
    }

    private fun Map<String, ByteArray>.string(key: String): String? =
        this[key]?.toString(Charsets.UTF_8)?.ifBlank { null }
}
