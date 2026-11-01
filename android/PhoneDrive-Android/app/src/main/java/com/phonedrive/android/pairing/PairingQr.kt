package com.phonedrive.android.pairing

import android.graphics.Bitmap
import com.google.zxing.BarcodeFormat
import com.google.zxing.EncodeHintType
import com.google.zxing.qrcode.QRCodeWriter
import com.google.zxing.qrcode.decoder.ErrorCorrectionLevel
import org.json.JSONObject

/**
 * 配对二维码内容（协议 pairing/README.md §2）：
 * {"v":1,"ip":"192.168.x.x","port":47821,"code":"123456",
 *  "deviceid":"A","name":"A","type":"phone","fp":"SHA256:..."}
 */
data class PairingPayload(
    val ip: String,
    val port: Int,
    val code: String,
    val deviceId: String,
    val name: String,
    val type: String,
    val fingerprint: String,
    val version: Int = 2,
) {
    fun toJson(): String = JSONObject().apply {
        put("v", version)
        put("ip", ip)
        put("port", port)
        put("code", code)
        put("deviceid", deviceId)
        put("name", name)
        put("type", type)
        put("fp", fingerprint)
    }.toString()

    companion object {
        /** 解析二维码文本；不是合法 PhoneDrive 配对内容时返回 null */
        fun parse(raw: String): PairingPayload? {
            val obj = runCatching { JSONObject(raw.trim()) }.getOrNull() ?: return null
            if (obj.optInt("v", 0) != 2) return null
            val ip = obj.optString("ip").trim()
            val port = obj.optInt("port", 0)
            val code = obj.optString("code").trim()
            val deviceId = obj.optString("deviceid").trim()
            val name = obj.optString("name").trim()
            val type = obj.optString("type").trim()
            val fp = obj.optString("fp").trim()
            if (ip.isEmpty() || port !in 1..65535 || code.length != 6 || !code.all { it.isDigit() }) return null
            if (deviceId.isEmpty() || name.isEmpty() || fp.startsWith("SHA256:").not()) return null
            return PairingPayload(
                ip = ip,
                port = port,
                code = code,
                deviceId = deviceId,
                name = name,
                type = if (type.isEmpty()) "phone" else type,
                fingerprint = fp,
            )
        }
    }
}

object PairingQr {

    /**
     * 生成黑白二维码位图（UTF-8、纠错等级 M、静默区 1 模块）。
     * 调用方应在 IO 线程调用，尺寸建议 512px 以上以保证扫码成功率。
     */
    fun render(text: String, sizePx: Int): Bitmap {
        require(sizePx > 0) { "二维码尺寸必须为正数" }
        val hints = mapOf(
            EncodeHintType.MARGIN to 1,
            EncodeHintType.CHARACTER_SET to "UTF-8",
            EncodeHintType.ERROR_CORRECTION to ErrorCorrectionLevel.M,
        )
        val matrix = QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, sizePx, sizePx, hints)
        val width = matrix.width
        val height = matrix.height
        val pixels = IntArray(width * height)
        for (y in 0 until height) {
            val row = y * width
            for (x in 0 until width) {
                pixels[row + x] = if (matrix.get(x, y)) 0xFF000000.toInt() else 0xFFFFFFFF.toInt()
            }
        }
        return Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888).apply {
            setPixels(pixels, 0, width, 0, 0, width, height)
        }
    }
}
