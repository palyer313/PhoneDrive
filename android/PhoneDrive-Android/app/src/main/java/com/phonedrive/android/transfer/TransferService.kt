package com.phonedrive.android.transfer

import android.app.Notification
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.IBinder
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import com.phonedrive.android.MainActivity
import com.phonedrive.android.PhoneDriveApp
import com.phonedrive.android.R
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.launch

/**
 * 传输前台服务（dataSync）：只承载通知与进程存活，实际任务由 TransferEngine 执行。
 * 通知展示总量进度；所有任务结束后自动停止。
 */
class TransferService : Service() {

    companion object {
        private const val NOTIFICATION_ID = 2001
        private const val ACTION_START = "com.phonedrive.android.action.START_TRANSFER_MONITOR"
        private const val ACTION_STOP = "com.phonedrive.android.action.STOP_TRANSFER_MONITOR"

        fun start(context: Context) {
            val intent = Intent(context, TransferService::class.java).apply { action = ACTION_START }
            runCatching { ContextCompat.startForegroundService(context, intent) }
        }

        fun stop(context: Context) {
            val intent = Intent(context, TransferService::class.java).apply { action = ACTION_STOP }
            runCatching { context.startService(intent) }
        }
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var observeJob: Job? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            stopSelf()
            return START_NOT_STICKY
        }
        startForegroundWithProgress(0, 0, true)
        observeJob?.cancel()
        observeJob = scope.launch {
            TransferEngine.get(this@TransferService).tasks.collect { tasks ->
                val active = tasks.filter { it.isActive }
                if (active.isEmpty()) {
                    ServiceCompat.stopForeground(this@TransferService, ServiceCompat.STOP_FOREGROUND_REMOVE)
                    stopSelf()
                } else {
                    val total = active.sumOf { if (it.size > 0) it.size else 0L }
                    val done = active.sumOf { it.transferred.coerceAtMost(it.size) }
                    val percent = if (total > 0) ((done * 100) / total).toInt().coerceIn(0, 100) else 0
                    startForegroundWithProgress(active.size, percent, total <= 0)
                }
            }
        }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        observeJob?.cancel()
        scope.cancel()
        super.onDestroy()
    }

    private fun startForegroundWithProgress(taskCount: Int, percent: Int, indeterminate: Boolean) {
        val openIntent = PendingIntent.getActivity(
            this, 0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        val builder = NotificationCompat
            .Builder(this, PhoneDriveApp.NOTIFICATION_CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_notification)
            .setLargeIcon(com.phonedrive.android.BrandIcons.bitmap(this))
            .setContentTitle("PhoneDrive 正在传输")
            .setContentText(
                if (taskCount <= 0) "准备中…" else "$taskCount 个任务进行中 · $percent%"
            )
            .setContentIntent(openIntent)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
        if (indeterminate) {
            builder.setProgress(0, 0, true)
        } else {
            builder.setProgress(100, percent, false)
        }
        val notification: Notification = builder.build()
        ServiceCompat.startForeground(
            this,
            NOTIFICATION_ID,
            notification,
            ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC
        )
        getSystemService(NotificationManager::class.java).notify(NOTIFICATION_ID, notification)
    }
}
