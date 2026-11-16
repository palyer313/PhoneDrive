package com.phonedrive.android.filemanager

import android.content.Context
import com.phonedrive.android.remote.DeviceDirectory
import com.phonedrive.android.remote.SftpClientSession
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.withContext
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.locks.ReentrantLock
import kotlin.concurrent.withLock

/**
 * 提供者中心：按 deviceId 复用 SFTP 会话，本地提供者单例。
 * 远端会话只在连接成功后才对外可见；地址变化会触发重连。
 *
 * 会话表是唯一连接状态来源。连接锁按设备隔离，某台设备的网络 IO 不会阻塞其他设备。
 */
object ProviderHub {

    @Volatile
    private var appContext: Context? = null

    private val localProvider: FileProvider by lazy {
        LocalFileProvider(requireNotNull(appContext) { "ProviderHub 未初始化" })
    }

    private val sessions = ConcurrentHashMap<String, SftpClientSession>()
    private val connectionLocks = ConcurrentHashMap<String, ReentrantLock>()
    private val connectionStateScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    val connected: StateFlow<Set<String>> = flow {
        while (true) {
            emit(sessions.filterValues { it.isConnected() }.keys.toSet())
            delay(1_000)
        }
    }.stateIn(
        scope = connectionStateScope,
        started = SharingStarted.WhileSubscribed(stopTimeoutMillis = 5_000),
        initialValue = emptySet(),
    )

    fun init(context: Context) {
        if (appContext == null) {
            appContext = context.applicationContext
        }
    }

    fun local(): FileProvider = localProvider

    fun isConnected(deviceId: String): Boolean =
        sessions[deviceId]?.isConnected() == true

    fun providerForBlocking(deviceId: String?): FileProvider {
        if (deviceId == null) return localProvider
        val context = requireNotNull(appContext) { "ProviderHub 未初始化" }
        val device = DeviceDirectory.getInstance(context).find(deviceId)
            ?: throw IllegalStateException("设备未配对，禁止连接")
        require(device.type != "windows") { "这台电脑是文件管理端，不提供远程文件共享" }

        return connectionLocks.getOrPut(deviceId) { ReentrantLock() }.withLock {
            val existing = sessions[deviceId]
            if (existing != null && existing.isConnected() && existing.remoteHost() == device.lastKnownIp) {
                return@withLock SftpFileProvider(existing, deviceId, device.name)
            }
            existing?.disconnect()
            sessions.remove(deviceId)

            val session = SftpClientSession(device)
            try {
                session.connect()
            } catch (error: Throwable) {
                runCatching { session.disconnect() }
                sessions.remove(deviceId)
                throw error
            }
            // Publish only after the complete handshake has succeeded.
            sessions[deviceId] = session
            SftpFileProvider(session, deviceId, device.name)
        }
    }

    suspend fun providerFor(deviceId: String?): FileProvider {
        if (deviceId == null) return localProvider
        return withContext(Dispatchers.IO) { providerForBlocking(deviceId) }
    }

    fun disconnect(deviceId: String) {
        connectionLocks.getOrPut(deviceId) { ReentrantLock() }.withLock {
            sessions.remove(deviceId)?.disconnect()
        }
    }

    fun disconnectAll() {
        sessions.keys.forEach { deviceId ->
            runCatching { disconnect(deviceId) }
        }
    }
}
