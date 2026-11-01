package com.phonedrive.android.settings

import android.content.Context

/**
 * 应用级设置。沿用 phonedrive_settings 以便和 BootReceiver 的自启动开关共享。
 */
object AppSettings {

    const val PREFS_NAME = "phonedrive_settings"

    /** 与 BootReceiver / 设置页共用 */
    const val KEY_AUTO_START = "auto_start_service"

    /** 传输并发数 1-8，默认 2 */
    const val KEY_TRANSFER_CONCURRENCY = "transfer_concurrency"

    /** 已配对设备自动重连，默认开启 */
    const val KEY_AUTO_RECONNECT = "auto_reconnect"

    fun prefs(context: Context) =
        context.applicationContext.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    fun isAutoStartEnabled(context: Context): Boolean =
        prefs(context).getBoolean(KEY_AUTO_START, false)

    fun setAutoStartEnabled(context: Context, enabled: Boolean) {
        prefs(context).edit().putBoolean(KEY_AUTO_START, enabled).apply()
    }

    fun transferConcurrency(context: Context): Int =
        prefs(context).getInt(KEY_TRANSFER_CONCURRENCY, 2).coerceIn(1, 8)

    fun setTransferConcurrency(context: Context, value: Int) {
        prefs(context).edit().putInt(KEY_TRANSFER_CONCURRENCY, value.coerceIn(1, 8)).apply()
    }

    fun isAutoReconnectEnabled(context: Context): Boolean =
        prefs(context).getBoolean(KEY_AUTO_RECONNECT, true)

    fun setAutoReconnectEnabled(context: Context, enabled: Boolean) {
        prefs(context).edit().putBoolean(KEY_AUTO_RECONNECT, enabled).apply()
    }
}
