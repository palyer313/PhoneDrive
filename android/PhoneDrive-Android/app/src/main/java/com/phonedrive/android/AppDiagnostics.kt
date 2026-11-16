package com.phonedrive.android

import android.content.Context
import android.util.Log
import java.io.File
import java.io.PrintWriter
import java.io.StringWriter
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone

/** Bounded local diagnostics. Never write private keys, pairing codes, or file contents. */
object AppDiagnostics {
    private const val TAG = "PhoneDriveDiag"
    private const val MAX_LOG_BYTES = 2L * 1024L * 1024L
    private val lock = Any()

    fun record(context: Context, category: String, error: Throwable) {
        val stack = StringWriter().also { writer ->
            PrintWriter(writer).use(error::printStackTrace)
        }.toString()
        write(context, category, error.javaClass.simpleName + ": " + (error.message ?: "no message") + "\n" + stack)
    }

    fun write(context: Context, category: String, message: String) {
        runCatching {
            synchronized(lock) {
                val dir = File(context.applicationContext.filesDir, "logs").apply { mkdirs() }
                val file = File(dir, "crash.log")
                if (file.length() > MAX_LOG_BYTES) {
                    file.renameTo(File(dir, "crash.log.previous"))
                }
                val timestamp = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSSZ", Locale.US).apply {
                    timeZone = TimeZone.getDefault()
                }.format(Date())
                file.appendText("[$timestamp] $category: $message\n")
            }
        }.onFailure { Log.w(TAG, "diagnostic write failed", it) }
    }
}
