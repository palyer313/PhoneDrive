package com.phonedrive.android.server

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import androidx.core.content.ContextCompat

/**
 * 开机自启动入口。
 * 注意：HyperOS / Android 14 对自启动有严格限制，用户可能需要在系统设置中
 * 授予"自启动"权限。若系统阻止启动，App 首页会引导用户手动开启服务。
 */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED) return
        val prefs = context.getSharedPreferences("phonedrive_settings", Context.MODE_PRIVATE)
        if (!prefs.getBoolean("auto_start_service", false)) return
        val serviceIntent = Intent(context, SftpServerService::class.java).apply {
            action = SftpServerService.ACTION_START
        }
        runCatching {
            ContextCompat.startForegroundService(context, serviceIntent)
        }
    }
}
