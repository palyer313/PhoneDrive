package com.phonedrive.android.ui

import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

fun formatSize(bytes: Long): String {
    if (bytes < 0) return "-"
    if (bytes < 1024) return "$bytes B"
    val units = arrayOf("KB", "MB", "GB", "TB", "PB")
    var value = bytes.toDouble()
    var index = -1
    do {
        value /= 1024.0
        index++
    } while (value >= 1024.0 && index < units.size - 1)
    return if (value >= 100) {
        String.format(Locale.US, "%.0f %s", value, units[index])
    } else {
        String.format(Locale.US, "%.1f %s", value, units[index])
    }
}

fun formatTime(millis: Long): String {
    if (millis <= 0L) return "-"
    return SimpleDateFormat("yyyy-MM-dd HH:mm", Locale.getDefault()).format(Date(millis))
}

fun formatSpeed(bytesPerSecond: Long): String {
    if (bytesPerSecond <= 0L) return "-"
    return formatSize(bytesPerSecond) + "/s"
}

fun formatEta(seconds: Long): String {
    if (seconds <= 0L) return "-"
    if (seconds < 60) return "${seconds}s"
    val minutes = seconds / 60
    if (minutes < 60) return "${minutes}m${(seconds % 60).toString().padStart(2, '0')}s"
    val hours = minutes / 60
    return "${hours}h${(minutes % 60).toString().padStart(2, '0')}m"
}

/** 读取最多 limit 字节（用于文本预览，避免整文件进内存） */
fun readLimited(input: InputStream, limit: Int): ByteArray {
    val output = ByteArrayOutputStream()
    val buffer = ByteArray(16 * 1024)
    while (output.size() < limit) {
        val read = input.read(buffer)
        if (read <= 0) break
        val take = minOf(read, limit - output.size())
        output.write(buffer, 0, take)
    }
    return output.toByteArray()
}

/** 简单二进制判定：前 4KB 内出现 NUL 即视为二进制 */
fun looksBinary(bytes: ByteArray): Boolean {
    val end = minOf(bytes.size, 4096)
    for (i in 0 until end) {
        if (bytes[i] == 0.toByte()) return true
    }
    return false
}
