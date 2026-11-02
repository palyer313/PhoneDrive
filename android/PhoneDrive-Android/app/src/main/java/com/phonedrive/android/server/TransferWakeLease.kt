package com.phonedrive.android.server

import android.content.Context
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.os.SystemClock

/** Data I/O keeps the CPU awake; idle SSH sessions and health checks do not. */
internal class TransferWakeLease(context: Context, private val enabled: () -> Boolean) : AutoCloseable {
    private val handler = Handler(Looper.getMainLooper())
    private val wake = (context.getSystemService(Context.POWER_SERVICE) as PowerManager)
        .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "PhoneDrive:transfer").apply { setReferenceCounted(false) }
    private var closed = false
    private var touchedAt = Long.MIN_VALUE
    private var acquiredAt = Long.MIN_VALUE
    private val expire = object : Runnable {
        override fun run() = synchronized(this@TransferWakeLease) {
            val idleFor = SystemClock.elapsedRealtime() - touchedAt
            if (!closed && idleFor < IDLE_MS) {
                handler.postDelayed(this, IDLE_MS - idleFor)
            } else if (wake.isHeld) wake.release()
            Unit
        }
    }

    @Synchronized fun touch() {
        if (closed || !enabled()) return
        val now = SystemClock.elapsedRealtime()
        if (touchedAt != Long.MIN_VALUE && now - touchedAt < 500L) return
        touchedAt = now
        if (!wake.isHeld || now - acquiredAt >= 60_000L) {
            if (wake.isHeld) wake.release()
            wake.acquire(120_000L) // independent safety timeout if callbacks fail
            acquiredAt = now
        }
        handler.removeCallbacks(expire)
        handler.postDelayed(expire, IDLE_MS)
    }

    @Synchronized override fun close() {
        closed = true
        handler.removeCallbacks(expire)
        if (wake.isHeld) wake.release()
    }

    companion object { private const val IDLE_MS = 15_000L }
}
