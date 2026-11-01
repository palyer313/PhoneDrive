package com.phonedrive.android.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.Settings
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Slider
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import com.phonedrive.android.remote.ClientKey
import com.phonedrive.android.security.DeviceIdentity
import com.phonedrive.android.server.AuthorizedKeysStore
import com.phonedrive.android.server.SftpServerService
import com.phonedrive.android.settings.AppSettings
import com.phonedrive.android.transfer.TransferEngine
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.net.NetworkInterface
import java.security.interfaces.RSAPublicKey
import kotlin.math.roundToInt

/**
 * 设置 / 服务页：保留 Phase 1 全部服务端能力，并补充客户端密钥与传输并发设置。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsScreen(onAutoReconnectChanged: () -> Unit = {}) {
    val context = LocalContext.current
    val status by SftpServerService.status.collectAsState()

    val hostKey = remember { DeviceIdentity.getOrCreateHostKeyPair() }
    val rsaPublic = hostKey.public as RSAPublicKey
    val fingerprint = remember { DeviceIdentity.fingerprintSha256(rsaPublic) }
    val deviceId = remember { DeviceIdentity.getDeviceId(context) }

    var authorizedKeys by remember { mutableStateOf(AuthorizedKeysStore(context).list()) }
    var showAddKeyDialog by remember { mutableStateOf(false) }

    var clientKeyLine by remember { mutableStateOf<String?>(null) }
    var clientFingerprint by remember { mutableStateOf<String?>(null) }
    var clientKeyLoading by remember { mutableStateOf(true) }

    var concurrency by remember { mutableStateOf(AppSettings.transferConcurrency(context)) }

    LaunchedEffect(Unit) {
        val loaded = runCatching {
            withContext(Dispatchers.IO) {
                ClientKey.getOrCreateKeyPair()
                ClientKey.openSshPublicKeyLine() to ClientKey.fingerprint()
            }
        }.getOrNull()
        clientKeyLine = loaded?.first
        clientFingerprint = loaded?.second
        clientKeyLoading = false
    }

    Scaffold(
        topBar = { TopAppBar(title = { Text("设置 / 服务") }) },
        // 外层已由底部导航消费系统栏，这里避免重复内边距
        contentWindowInsets = WindowInsets(0, 0, 0, 0)
    ) { padding ->
        Column(
            modifier = Modifier
                .padding(padding)
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            // 服务状态
            Card(modifier = Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text("文件服务", style = MaterialTheme.typography.titleMedium)
                    Text(
                        when (status.state) {
                            SftpServerService.State.RUNNING -> "运行中 · 端口 ${status.port}"
                            SftpServerService.State.STARTING -> "启动中…"
                            SftpServerService.State.ERROR -> "错误: ${status.error ?: "未知"}"
                            SftpServerService.State.STOPPED -> "已停止"
                        },
                        color = when (status.state) {
                            SftpServerService.State.RUNNING -> MaterialTheme.colorScheme.primary
                            SftpServerService.State.ERROR -> MaterialTheme.colorScheme.error
                            else -> MaterialTheme.colorScheme.onSurfaceVariant
                        }
                    )
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        if (status.state != SftpServerService.State.RUNNING) {
                            Button(onClick = { startSftpService(context) }) { Text("启动服务") }
                        } else {
                            OutlinedButton(onClick = { stopSftpService(context) }) { Text("停止服务") }
                        }
                    }
                    Text(
                        "其他设备需先把本机客户端公钥加入其授权列表，才能被本机访问。",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }

            // 设备信息
            Card(modifier = Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    Text("设备信息", style = MaterialTheme.typography.titleMedium)
                    InfoRow("设备", "${Build.MANUFACTURER} ${Build.MODEL}")
                    InfoRow("局域网 IP", localIpAddresses().joinToString(", ").ifBlank { "未连接" })
                    InfoRow("SFTP 端口", SftpServerService.DEFAULT_PORT.toString())
                    Text("Host Key 指纹", style = MaterialTheme.typography.labelLarge)
                    Text(fingerprint, fontFamily = FontFamily.Monospace,
                        style = MaterialTheme.typography.bodySmall)
                    Text("Device ID", style = MaterialTheme.typography.labelLarge)
                    Text(deviceId, fontFamily = FontFamily.Monospace,
                        style = MaterialTheme.typography.bodySmall)
                }
            }

            // 客户端密钥（配对用）
            Card(modifier = Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text("我的客户端公钥", style = MaterialTheme.typography.titleMedium)
                    Text(
                        "配对时把这行公钥加入对方设备的\"已授权公钥\"，对方才能接受本机连接。",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                    when {
                        clientKeyLoading -> Text("正在生成客户端密钥…", style = MaterialTheme.typography.bodySmall)
                        clientKeyLine == null -> Text(
                            "客户端密钥不可用，请重启应用后重试。",
                            color = MaterialTheme.colorScheme.error,
                            style = MaterialTheme.typography.bodySmall
                        )
                        else -> {
                            Text(
                                clientKeyLine ?: "",
                                fontFamily = FontFamily.Monospace,
                                style = MaterialTheme.typography.bodySmall
                            )
                            clientFingerprint?.let {
                                Text("客户端指纹", style = MaterialTheme.typography.labelLarge)
                                Text(it, fontFamily = FontFamily.Monospace,
                                    style = MaterialTheme.typography.bodySmall)
                            }
                            OutlinedButton(onClick = {
                                val line = clientKeyLine ?: return@OutlinedButton
                                val manager = context.getSystemService(Context.CLIPBOARD_SERVICE)
                                    as ClipboardManager
                                manager.setPrimaryClip(ClipData.newPlainText("PhoneDrive 客户端公钥", line))
                            }) { Text("复制公钥") }
                        }
                    }
                }
            }

            // 传输并发
            Card(modifier = Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text("传输并发任务数", style = MaterialTheme.typography.titleMedium)
                    Text("$concurrency 个（范围 1-8，默认 2）",
                        style = MaterialTheme.typography.bodyMedium)
                    Slider(
                        value = concurrency.toFloat(),
                        onValueChange = { concurrency = it.roundToInt() },
                        valueRange = 1f..8f,
                        steps = 6,
                        onValueChangeFinished = {
                            AppSettings.setTransferConcurrency(context, concurrency)
                            TransferEngine.get(context).requestDispatch()
                        }
                    )
                    Text("并发过高可能让对方 SSH 服务负载过重，建议保持 2。",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }

            // 自动重连
            Card(modifier = Modifier.fillMaxWidth()) {
                Row(
                    Modifier.padding(16.dp).fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column(Modifier.weight(1f)) {
                        Text("已配对设备自动重连", style = MaterialTheme.typography.titleMedium)
                        Text(
                            "设备在局域网重新上线且 Host Key 指纹一致时自动连接；" +
                                "连接失败按 1s→2s→4s→8s→16s（上限 30s）退避重试，认证失败不重试。",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                    var autoReconnect by remember {
                        mutableStateOf(AppSettings.isAutoReconnectEnabled(context))
                    }
                    Switch(checked = autoReconnect, onCheckedChange = {
                        autoReconnect = it
                        AppSettings.setAutoReconnectEnabled(context, it)
                        onAutoReconnectChanged()
                    })
                }
            }

            // 系统文件管理器集成
            Card(modifier = Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    Text("系统文件管理器集成（实验）", style = MaterialTheme.typography.titleMedium)
                    Text(
                        "已注册系统文件提供器，" +
                            "每台已配对设备会作为一个存储位置出现在支持该接口的文件选择器中。",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                    Text(
                        "注意：小米 / HyperOS 自带文件管理器是否展示第三方 DocumentsProvider 需实机验证，" +
                            "本应用不保证在自带文件管理器中一定可见；可先用系统文件选择器 / 其他支持 " +
                            "Storage Access Framework 的应用验证。",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.error
                    )
                }
            }

            // 存储权限
            Card(modifier = Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text("共享存储访问", style = MaterialTheme.typography.titleMedium)
                    val hasAccess = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                        Environment.isExternalStorageManager()
                    } else {
                        true
                    }
                    Text(if (hasAccess) "已授权" else "未授权 — 需要授予\"所有文件访问\"才能浏览与共享存储")
                    if (!hasAccess) {
                        OutlinedButton(onClick = {
                            val intent = Intent(
                                Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION,
                                Uri.parse("package:${context.packageName}")
                            )
                            runCatching { context.startActivity(intent) }
                        }) { Text("去授权") }
                    }
                }
            }

            Card(modifier = Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text("共享范围与权限", style = MaterialTheme.typography.titleMedium)
                    var root by remember { mutableStateOf(com.phonedrive.android.server.SharePolicy.prefs(context).getString("root", "") ?: "") }
                    var readOnly by remember { mutableStateOf(com.phonedrive.android.server.SharePolicy.readOnly(context, null)) }
                    var message by remember { mutableStateOf<String?>(null) }
                    Text("填写共享存储内的目录，例如 Download、DCIM；留空共享存储根目录。目录修改后需停止并重新启动服务。", style = MaterialTheme.typography.bodySmall)
                    OutlinedTextField(value = root, onValueChange = { root = it }, label = { Text("共享子目录") }, modifier = Modifier.fillMaxWidth())
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text("所有已授权设备只读", modifier = Modifier.weight(1f))
                        Switch(checked = readOnly, onCheckedChange = {
                            readOnly = it
                            com.phonedrive.android.server.SharePolicy.prefs(context).edit().putBoolean("read_only", it).apply()
                        })
                    }
                    OutlinedButton(onClick = {
                        val prefs = com.phonedrive.android.server.SharePolicy.prefs(context)
                        val old = prefs.getString("root", "")
                        prefs.edit().putString("root", root.trim().trim('/')).commit()
                        message = runCatching { com.phonedrive.android.server.SharePolicy.root(context); "共享目录已保存，重启文件服务后生效" }
                            .getOrElse { prefs.edit().putString("root", old).commit(); it.message ?: "目录无效" }
                    }) { Text("保存共享范围") }
                    message?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
                }
            }

            // 已授权公钥
            Card(modifier = Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text("已授权设备 (${authorizedKeys.size})",
                        style = MaterialTheme.typography.titleMedium)
                    if (authorizedKeys.isEmpty()) {
                        Text("暂无授权设备。添加对端设备的客户端公钥后，对方才能访问本机。",
                            style = MaterialTheme.typography.bodySmall)
                    } else {
                        authorizedKeys.forEach { entry ->
                            Row(
                                verticalAlignment = Alignment.CenterVertically,
                                horizontalArrangement = Arrangement.spacedBy(8.dp)
                            ) {
                                Column(Modifier.weight(1f)) {
                                    Text(entry.comment.ifBlank { "(未命名设备)" },
                                        style = MaterialTheme.typography.bodyMedium)
                                    Text(entry.blobBase64.take(32) + "…",
                                        fontFamily = FontFamily.Monospace,
                                        style = MaterialTheme.typography.bodySmall)
                                }
                                var deviceReadOnly by remember(entry.blobBase64) {
                                    mutableStateOf(com.phonedrive.android.server.SharePolicy.prefs(context).getBoolean("readonly_${entry.blobBase64}", false))
                                }
                                Column {
                                    Text("只读", style = MaterialTheme.typography.labelSmall)
                                    Switch(checked = deviceReadOnly, onCheckedChange = {
                                        deviceReadOnly = it
                                        com.phonedrive.android.server.SharePolicy.prefs(context).edit().putBoolean("readonly_${entry.blobBase64}", it).apply()
                                    })
                                }
                                TextButton(onClick = {
                                    val store = AuthorizedKeysStore(context)
                                    store.remove(entry.blobBase64)
                                    authorizedKeys = store.list()
                                }) { Text("撤销") }
                            }
                        }
                    }
                    OutlinedButton(onClick = { showAddKeyDialog = true }) {
                        Text("添加公钥")
                    }
                }
            }

            Card(modifier = Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    var keepAwake by remember { mutableStateOf(AppSettings.prefs(context).getBoolean("screen_off_service", true)) }
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text("熄屏保持共享服务", modifier = Modifier.weight(1f))
                        Switch(checked = keepAwake, onCheckedChange = {
                            keepAwake = it
                            AppSettings.prefs(context).edit().putBoolean("screen_off_service", it).apply()
                        })
                    }
                    Text("共享服务运行时保持处理器工作，不点亮屏幕；会增加耗电。修改后重启共享服务生效。系统强制停止后需手动打开应用。", style = MaterialTheme.typography.bodySmall)
                    OutlinedButton(onClick = {
                        runCatching { context.startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS)) }
                    }) { Text("设置电池优化白名单") }
                    OutlinedButton(onClick = {
                        val vendor = Intent().setComponent(android.content.ComponentName("com.miui.securitycenter", "com.miui.permcenter.autostart.AutoStartManagementActivity"))
                        if (runCatching { context.startActivity(vendor) }.isFailure)
                            runCatching { context.startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:" + context.packageName))) }
                    }) { Text("设置系统自启动 / 后台权限") }
                    Text("小米 / HyperOS：允许自启动，电量管理设为无限制；每台手机和平板分别设置。", style = MaterialTheme.typography.bodySmall)
                }
            }

            // 自启动
            Card(modifier = Modifier.fillMaxWidth()) {
                Row(
                    Modifier.padding(16.dp).fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column(Modifier.weight(1f)) {
                        Text("开机自动连接已配对电脑", style = MaterialTheme.typography.titleMedium)
                        Text("开启后，本机重启会启动共享服务，已配对电脑可自动连接；关闭后需手动启动。HyperOS 还需允许系统自启动。",
                            style = MaterialTheme.typography.bodySmall)
                    }
                    var autoStart by remember {
                        mutableStateOf(AppSettings.isAutoStartEnabled(context))
                    }
                    Switch(checked = autoStart, onCheckedChange = {
                        autoStart = it
                        AppSettings.setAutoStartEnabled(context, it)
                    })
                }
            }

            Spacer(Modifier.height(24.dp))
        }
    }

    if (showAddKeyDialog) {
        AddKeyDialog(
            onDismiss = { showAddKeyDialog = false },
            onAdd = { line, comment ->
                val store = AuthorizedKeysStore(context)
                store.add(line, comment)
                authorizedKeys = store.list()
                showAddKeyDialog = false
            }
        )
    }
}

@Composable
private fun AddKeyDialog(onDismiss: () -> Unit, onAdd: (String, String) -> Unit) {
    var keyLine by remember { mutableStateOf("") }
    var comment by remember { mutableStateOf("") }
    AlertDialog(
        icon = { BrandDialogIcon() },
        onDismissRequest = onDismiss,
        title = { Text("添加授权公钥") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedTextField(
                    value = comment,
                    onValueChange = { comment = it },
                    label = { Text("设备备注（如：小米平板）") },
                    singleLine = true
                )
                OutlinedTextField(
                    value = keyLine,
                    onValueChange = { keyLine = it },
                    label = { Text("OpenSSH 公钥（ssh-rsa AAAA…）") },
                    minLines = 3
                )
            }
        },
        confirmButton = {
            TextButton(
                onClick = { onAdd(keyLine, comment) },
                enabled = keyLine.trim().startsWith("ssh-")
            ) { Text("添加") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("取消") } }
    )
}

@Composable
private fun InfoRow(label: String, value: String) {
    Row {
        Text(label, Modifier.weight(0.35f), style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(value, Modifier.weight(0.65f), style = MaterialTheme.typography.bodyMedium)
    }
}

private fun startSftpService(context: Context) {
    val intent = Intent(context, SftpServerService::class.java).apply {
        action = SftpServerService.ACTION_START
    }
    androidx.core.content.ContextCompat.startForegroundService(context, intent)
}

private fun stopSftpService(context: Context) {
    val intent = Intent(context, SftpServerService::class.java).apply {
        action = SftpServerService.ACTION_STOP
    }
    context.startService(intent)
}

private fun localIpAddresses(): List<String> = runCatching {
    NetworkInterface.getNetworkInterfaces().toList()
        .filter { it.isUp && !it.isLoopback }
        .flatMap { it.inetAddresses.toList() }
        .filter { !it.isLoopbackAddress && it.hostAddress?.contains(':') == false }
        .mapNotNull { it.hostAddress }
}.getOrDefault(emptyList())
