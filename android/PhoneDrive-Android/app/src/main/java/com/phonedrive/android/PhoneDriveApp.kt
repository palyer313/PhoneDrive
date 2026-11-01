package com.phonedrive.android

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import com.phonedrive.android.filemanager.ProviderHub
import com.phonedrive.android.transfer.TransferEngine

class PhoneDriveApp : Application() {

    /**
     * 必须在这里设置而不是 onCreate：
     * Android 进程启动顺序是 Application.attachBaseContext → ContentProvider.onCreate
     * → Application.onCreate。DocumentsProvider 若在 onCreate 之前被系统查询并触碰到
     * SSH 相关类，就会在 user.home 尚未注入时抛出 ExceptionInInitializerError。
     */
    override fun attachBaseContext(base: android.content.Context?) {
        installSshdCompat(base ?: this)
        super.attachBaseContext(base)
    }

    override fun onCreate() {
        super.onCreate()
        instance = this
        ProviderHub.init(this)
        // 提前恢复未完成的传输任务（状态为已暂停，等待用户继续）
        TransferEngine.get(this)
        val channel = NotificationChannel(
            NOTIFICATION_CHANNEL_ID,
            getString(R.string.notification_channel_service),
            NotificationManager.IMPORTANCE_LOW
        ).apply { description = getString(R.string.notification_channel_service_desc) }
        getSystemService(android.app.NotificationManager::class.java)
            .createNotificationChannel(channel)
    }

    /**
     * 让 Apache SSHD 在 Android 上具备可用的 home 目录语义。
     * SSHD 的 ServerBuilder / DefaultAuthorizedKeysAuthenticator 会在静态初始化阶段
     * 读取 System.getProperty("user.home") 推导默认密钥目录；Android 上该值为空，
     * 抛出 IllegalArgumentException("No user home") → ExceptionInInitializerError。
     * 这里只指向 App 私有目录，避免 SSHD 触碰真实用户目录。
     */
    private fun installSshdCompat(context: android.content.Context) {
        val home = context.filesDir?.absolutePath ?: return
        System.setProperty("user.home", home)
        if (System.getProperty("user.dir").isNullOrEmpty()) {
            System.setProperty("user.dir", home)
        }
    }

    companion object {
        const val NOTIFICATION_CHANNEL_ID = "phonedrive_service"
        lateinit var instance: PhoneDriveApp
            private set
    }
}
