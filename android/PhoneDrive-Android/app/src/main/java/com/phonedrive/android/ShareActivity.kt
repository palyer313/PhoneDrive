package com.phonedrive.android

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.provider.OpenableColumns
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.lifecycle.lifecycleScope
import com.phonedrive.android.remote.DeviceDirectory
import com.phonedrive.android.remote.PairedDevice
import com.phonedrive.android.server.SharePolicy
import com.phonedrive.android.server.SftpServerService
import com.phonedrive.android.ui.theme.PhoneDriveTheme
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.first
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.security.MessageDigest
import java.util.UUID

class ShareActivity : ComponentActivity() {
    private var message by mutableStateOf("请选择接收电脑。电脑需运行新版 PhoneDrive 并连接本机。")
    private var busy by mutableStateOf(false)
    private var queued by mutableStateOf(false)
    private var selected by mutableStateOf<String?>(null)
    private lateinit var uris: List<Uri>

    @Suppress("DEPRECATION")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        uris = when (intent.action) {
            Intent.ACTION_SEND -> listOfNotNull(intent.getParcelableExtra<Uri>(Intent.EXTRA_STREAM))
            Intent.ACTION_SEND_MULTIPLE -> intent.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM)?.toList() ?: emptyList()
            else -> emptyList()
        }.ifEmpty { intent.clipData?.let { clip -> (0 until clip.itemCount).mapNotNull { clip.getItemAt(it).uri } } ?: emptyList() }
            .distinct()
        val devices = DeviceDirectory.getInstance(this).list().filter { it.type == "windows" }
        selected = devices.singleOrNull()?.deviceId
        setContent {
            PhoneDriveTheme {
                Surface(Modifier.fillMaxSize()) {
                    Column(Modifier.padding(24.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                        Spacer(Modifier.height(16.dp))
                        Text("通过 PhoneDrive 分享", style = MaterialTheme.typography.headlineSmall)
                        Text("${uris.size} 个文件 / 图片", style = MaterialTheme.typography.titleMedium)
                        if (devices.isEmpty()) Text("尚未配对电脑，请先在 PhoneDrive 中扫码配对，再重新分享。")
                        devices.forEach { device ->
                            FilterChip(selected = selected == device.deviceId, enabled = !busy && !queued,
                                onClick = { selected = device.deviceId }, label = { Text(device.name) })
                        }
                        Text(message)
                        if (busy) LinearProgressIndicator(Modifier.fillMaxWidth())
                        Button(enabled = !busy && !queued && selected != null && uris.size in 1..100, onClick = {
                            devices.firstOrNull { it.deviceId == selected }?.let { send(it) }
                        }) { Text(if (queued) "已加入分享队列" else "发送到电脑") }
                        OutlinedButton(onClick = { startActivity(Intent(this@ShareActivity, MainActivity::class.java)) }) { Text("打开 PhoneDrive") }
                        TextButton(enabled = !busy, onClick = { finish() }) { Text("关闭") }
                        Text("文件会暂存到当前共享范围内的 .phonedrive-share 文件夹；接收成功后自动清理这次暂存，原文件保持不变。若电脑设置了手动连接，请在电脑端先连接手机。", style = MaterialTheme.typography.bodySmall)
                    }
                }
            }
        }
    }

    private fun send(device: PairedDevice) {
        busy = true
        lifecycleScope.launch {
            var batch: File? = null
            try {
                require(uris.all { it.scheme == "content" }) { "此分享来源不提供可读取的文件，请从相册或文件管理器重新分享。" }
                require(runCatching { UUID.fromString(device.deviceId) }.isSuccess) { "电脑标识无效，请重新配对。" }
                val root = SharePolicy.root(this@ShareActivity)
                if (SftpServerService.status.value.state == SftpServerService.State.RUNNING) {
                    require(SftpServerService.activeShareRoot == root.canonicalPath) { "共享范围已修改，请先重启手机文件服务再分享。" }
                } else {
                    ContextCompat.startForegroundService(this@ShareActivity, Intent(this@ShareActivity, SftpServerService::class.java).setAction(SftpServerService.ACTION_START))
                    val status = withTimeout(20000) { SftpServerService.status.first { it.state == SftpServerService.State.RUNNING || it.state == SftpServerService.State.ERROR } }
                    check(status.state == SftpServerService.State.RUNNING) { status.error ?: "文件服务启动失败" }
                }
                val id = UUID.randomUUID().toString().replace("-", "")
                message = "正在准备分享文件…"
                batch = withContext(Dispatchers.IO) {
                    val directory = File(root, ".phonedrive-share/${device.deviceId}/$id")
                    require(directory.canonicalPath.startsWith(root.canonicalPath + File.separator))
                    check(directory.mkdirs()) { "无法创建分享暂存目录，请检查存储权限。" }
                    try {
                        val files = JSONArray()
                        uris.forEachIndexed { index, uri ->
                            ensureActive()
                            var name = "分享文件-${index + 1}"
                            contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { cursor ->
                                if (cursor.moveToFirst()) cursor.getString(0)?.takeIf { it.isNotBlank() }?.let { name = it.take(240) }
                            }
                            val digest = MessageDigest.getInstance("SHA-256")
                            val fileName = "%03d.bin".format(index)
                            val output = File(directory, fileName)
                            var length = 0L
                            (contentResolver.openInputStream(uri) ?: error("无法读取 $name")).use { input ->
                                output.outputStream().use { target ->
                                    val buffer = ByteArray(256 * 1024)
                                    while (true) {
                                        ensureActive()
                                        val count = input.read(buffer); if (count < 0) break
                                        target.write(buffer, 0, count); digest.update(buffer, 0, count); length += count
                                    }
                                }
                            }
                            files.put(JSONObject().put("name", name).put("file", fileName).put("size", length)
                                .put("sha256", digest.digest().joinToString("") { "%02x".format(it.toInt() and 255) }))
                        }
                        val manifest = JSONObject().put("v", 1).put("id", id).put("target", device.deviceId).put("files", files)
                        File(directory, "request.tmp").writeText(manifest.toString())
                        check(File(directory, "request.tmp").renameTo(File(directory, "request.json")))
                        directory
                    } catch (e: Throwable) { directory.deleteRecursively(); throw e }
                }
                queued = true; busy = false
                message = "已准备好，等待 ${device.name} 接收。首次接收时请在电脑上选择保存位置。"
                while (isActive) {
                    delay(2000)
                    val receipt = withContext(Dispatchers.IO) { File(batch, "received.json").takeIf { it.exists() }?.let { runCatching { JSONObject(it.readText()).optBoolean("ok") }.getOrDefault(false) } ?: false }
                    if (receipt) {
                        withContext(Dispatchers.IO) { batch.deleteRecursively() }
                        message = "电脑已校验并保存 ${uris.size} 个文件，分享完成。"
                        break
                    }
                }
            } catch (e: CancellationException) { throw e }
            catch (e: Exception) { message = "分享未完成：${e.message ?: "未知错误"}" }
            finally { busy = false }
        }
    }
}
