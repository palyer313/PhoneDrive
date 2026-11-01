package com.phonedrive.android.filemanager

import android.content.Context
import com.phonedrive.android.remote.DeviceDirectory
import com.phonedrive.android.remote.SftpClientSession
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.util.concurrent.ConcurrentHashMap

/**
 * 提供者中心：按 deviceId 复用 SFTP 会话，本地提供者单例。
 * 远端会话只在连接成功后才对外可见；地址变化会触发重连。
 */
object ProviderHub {

    @Volatile
    private var appContext: Context? = null

    private val localProvider: FileProvider by lazy {
        LocalFileProvider(requireNotNull(appContext) { "ProviderHub 未初始化" })
    }

    private val sessions = ConcurrentHashMap<String, SftpClientSession>()
    private val bastion = Mutex()

    private val _connected = MutableStateFlow<Set<String>>(emptySet())
    val connected: StateFlow<Set<String>> = _connected.asStateFlow()

    fun init(context: Context) {
        if (appContext == null) {
            appContext = context.applicationContext
        }
    }

    fun local(): FileProvider = localProvider

    fun isConnected(deviceId: String): Boolean =
        sessions[deviceId]?.isConnected() == true

    @Synchronized
    fun providerForBlocking(deviceId: String?): FileProvider {
        if (deviceId == null) return localProvider
        val context = requireNotNull(appContext) { "ProviderHub 未初始化" }
        val device = DeviceDirectory.getInstance(context).find(deviceId)
            ?: throw IllegalStateException("设备未配对，禁止连接")
        require(device.type != "windows") { "这台电脑是文件管理端，不提供远程文件共享" }
        val existing = sessions[deviceId]
        if (existing != null && existing.isConnected() && existing.remoteHost() == device.lastKnownIp) {
            return SftpFileProvider(existing, deviceId, device.name)
        }
        existing?.disconnect()
        sessions.remove(deviceId)
        val session = SftpClientSession(device)
        session.connect()
        sessions[deviceId] = session
        _connected.value = _connected.value + deviceId
        return SftpFileProvider(session, deviceId, device.name)
    }

    suspend fun providerFor(deviceId: String?): FileProvider {
        if (deviceId == null) return localProvider
        return withContext(Dispatchers.IO) {
            bastion.withLock { providerForBlocking(deviceId) }
        }
    }

    fun disconnect(deviceId: String) {
        sessions.remove(deviceId)?.disconnect()
        _connected.value = _connected.value - deviceId
    }

    fun disconnectAll() {
        sessions.values.forEach { runCatching { it.disconnect() } }
        sessions.clear()
        _connected.value = emptySet()
    }
}
