package com.phonedrive.android.server

import android.app.Notification
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Environment
import android.os.IBinder
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import com.phonedrive.android.MainActivity
import com.phonedrive.android.PhoneDriveApp
import com.phonedrive.android.R
import com.phonedrive.android.discovery.NsdAdvertiser
import com.phonedrive.android.security.DeviceIdentity
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.launch
import org.apache.sshd.common.file.virtualfs.VirtualFileSystemFactory
import org.apache.sshd.common.keyprovider.KeyPairProvider
import org.apache.sshd.common.session.SessionContext
import org.apache.sshd.server.SshServer
import org.apache.sshd.server.auth.password.PasswordAuthenticator
import org.apache.sshd.server.auth.pubkey.PublickeyAuthenticator
import org.apache.sshd.sftp.server.SftpSubsystemFactory
import java.security.KeyPair

/**
 * Phase 1 核心：SFTP 服务端前台服务。
 * - Apache MINA SSHD，仅 publickey 认证（密码一律拒绝）
 * - 不开放 shell/exec，只挂 SFTP 子系统
 * - VirtualFileSystemFactory 将会话根目录限定在共享存储根目录
 * - 服务启动后同步注册 mDNS（_phonedrive._tcp）
 */
class SftpServerService : Service() {

    enum class State { STOPPED, STARTING, RUNNING, ERROR }

    data class Status(
        val state: State = State.STOPPED,
        val port: Int = DEFAULT_PORT,
        val error: String? = null,
    )

    companion object {
        private const val TAG = "SftpServerService"
        const val DEFAULT_PORT = 2222
        private const val NOTIFICATION_ID = 1001
        const val ACTION_START = "com.phonedrive.android.action.START_SERVER"
        const val ACTION_STOP = "com.phonedrive.android.action.STOP_SERVER"

        val status = MutableStateFlow(Status())
        private val AUTHORIZED_BLOB = org.apache.sshd.common.AttributeRepository.AttributeKey<String>()
        @Volatile private var liveServer: SshServer? = null
        @Volatile private var liveService: SftpServerService? = null
        @JvmStatic fun noteTransferActivity() { liveService?.transferWake?.touch() }
        fun revokeConnections(blob: String) {
            liveServer?.activeSessions?.filter { it.getAttribute(AUTHORIZED_BLOB) == blob }?.forEach { it.close(true) }
        }
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var sshd: SshServer? = null
    private val startGeneration = java.util.concurrent.atomic.AtomicInteger()
    private var transferWake: TransferWakeLease? = null
    private var nsdAdvertiser: NsdAdvertiser? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val recover = getSharedPreferences("phonedrive_settings", MODE_PRIVATE).getBoolean("recover_service", false)
        Log.i(TAG, "service command action=${intent?.action ?: "system-restart"} id=$startId recover=$recover")
        if (intent == null && !recover) {
            stopSelf()
            return START_NOT_STICKY
        }
        when (intent?.action) {
            ACTION_STOP -> {
                stopServer()
                stopSelf()
                return START_NOT_STICKY
            }
            else -> {
                startForegroundWithNotification()
                startServer()
                return if (recover) START_STICKY else START_NOT_STICKY
            }
        }
    }

    override fun onDestroy() {
        stopServer()
        scope.cancel()
        super.onDestroy()
    }

    private fun startForegroundWithNotification() {
        val openIntent = PendingIntent.getActivity(
            this, 0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE
        )
        val notification: Notification = NotificationCompat
            .Builder(this, PhoneDriveApp.NOTIFICATION_CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_notification)
            .setLargeIcon(com.phonedrive.android.BrandIcons.bitmap(this))
            .setContentTitle(getString(R.string.notification_title))
            .setContentText(getString(R.string.notification_text, DEFAULT_PORT))
            .setContentIntent(openIntent)
            .setOngoing(true)
            .build()
        ServiceCompat.startForeground(
            this, NOTIFICATION_ID, notification,
            ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE
        )
    }

    private fun startServer() {
        if (status.value.state == State.RUNNING || status.value.state == State.STARTING) return
        status.value = Status(State.STARTING, DEFAULT_PORT)
        val generation = startGeneration.incrementAndGet()
        scope.launch {
            synchronized(this@SftpServerService) {
            if (startGeneration.get() != generation) return@synchronized
            try {
                val hostKey = DeviceIdentity.getOrCreateHostKeyPair()
                val authorizedKeys = AuthorizedKeysStore(this@SftpServerService)
                val sharedRoot = SharePolicy.root(this@SftpServerService).toPath()

                val server = SshServer.setUpDefaultServer().apply {
                    host = "0.0.0.0"
                    port = DEFAULT_PORT
                    addSessionListener(CryptoDiagnosticsListener())

                    keyPairProvider = object : KeyPairProvider {
                        override fun loadKeys(session: SessionContext): Iterable<KeyPair> =
                            listOf(hostKey)

                        override fun loadKey(session: SessionContext, keyType: String): KeyPair? =
                            if (keyType == KeyPairProvider.SSH_RSA) hostKey else null

                        override fun getKeyTypes(session: SessionContext): Iterable<String> =
                            listOf(KeyPairProvider.SSH_RSA)
                    }

                    // 仅公钥认证；显式拒绝一切密码认证
                    publickeyAuthenticator = PublickeyAuthenticator { username, key, session ->
                        val allowed = username == "phonedrive" && authorizedKeys.isAuthorized(key)
                        if (allowed && key is java.security.interfaces.RSAPublicKey)
                            session.setAttribute(AUTHORIZED_BLOB, DeviceIdentity.toOpenSshPublicKey(key).split(' ')[1])
                        allowed
                    }
                    passwordAuthenticator = PasswordAuthenticator { _, _, _ -> false }

                    val sftpFactory = CapacitySftpFactory {
                        val stat = android.os.StatFs(sharedRoot.toString())
                        longArrayOf(stat.blockSizeLong, stat.blockCountLong, stat.freeBlocksLong, stat.availableBlocksLong)
                    }
                    sftpFactory.fileSystemAccessor = ShareFileSystemAccessor { session ->
                        SharePolicy.readOnly(this@SftpServerService, session.getAttribute(AUTHORIZED_BLOB))
                    }
                    sftpFactory.addSftpEventListener(TransferActivityListener())
                    subsystemFactories = listOf(sftpFactory)
                    // 不设置 shellFactory / commandFactory —— 禁用 shell 与 exec

                    fileSystemFactory = VirtualFileSystemFactory(sharedRoot)
                }
                server.start()
                sshd = server
                liveServer = server
                transferWake = TransferWakeLease(this@SftpServerService) {
                    getSharedPreferences("phonedrive_settings", MODE_PRIVATE).getBoolean("screen_off_service", true)
                }
                liveService = this@SftpServerService

                // mDNS 广播
                val fingerprint = DeviceIdentity.fingerprintSha256(
                    hostKey.public as java.security.interfaces.RSAPublicKey
                )
                val advertiser = NsdAdvertiser(this@SftpServerService).also {
                    it.start(
                        port = DEFAULT_PORT,
                        deviceId = DeviceIdentity.getDeviceId(this@SftpServerService),
                        deviceName = android.os.Build.MODEL,
                        deviceType = "phone",
                        fingerprint = fingerprint
                    )
                }
                nsdAdvertiser = advertiser

                status.value = Status(State.RUNNING, DEFAULT_PORT)
            } catch (e: Throwable) {
                // 捕获 Error（如 ExceptionInInitializerError）：服务启动失败不应让整个 App 崩溃
                stopServer()
                Log.e(TAG, "SFTP 服务启动失败", e)
                status.value = Status(State.ERROR, DEFAULT_PORT, e.message ?: e.javaClass.simpleName)
            }
            }
        }
    }

    @Synchronized
    private fun stopServer() {
        startGeneration.incrementAndGet()
        if (liveService === this) liveService = null
        transferWake?.close(); transferWake = null
        nsdAdvertiser?.stop()
        nsdAdvertiser = null
        val server = sshd
        sshd = null
        liveServer = null
        if (server != null) {
            // 必须同步停止：onDestroy 会立刻 cancel scope，
            // 若塞进 scope.launch 可能尚未调度就被取消，导致 2222 端口泄漏
            runCatching { server.stop(true) }
        }
        status.value = Status(State.STOPPED, DEFAULT_PORT)
    }
}
