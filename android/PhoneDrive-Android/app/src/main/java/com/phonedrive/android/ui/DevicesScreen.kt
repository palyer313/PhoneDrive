package com.phonedrive.android.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.graphics.Bitmap
import android.os.Build
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import com.phonedrive.android.filemanager.ProviderHub
import com.phonedrive.android.pairing.PairingClient
import com.phonedrive.android.pairing.PairingException
import com.phonedrive.android.pairing.PairingPayload
import com.phonedrive.android.pairing.PairingQr
import com.phonedrive.android.pairing.PairingServer
import com.phonedrive.android.pairing.PairingSession
import com.phonedrive.android.remote.ClientKey
import com.phonedrive.android.remote.ConnectionSupervisor
import com.phonedrive.android.remote.DeviceDirectory
import com.phonedrive.android.remote.DiscoveredPeer
import com.phonedrive.android.remote.PairedDevice
import com.phonedrive.android.security.DeviceIdentity
import com.phonedrive.android.server.AuthorizedKeysStore
import com.phonedrive.android.server.SftpServerService
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.security.interfaces.RSAPublicKey

/**
 * 设备页：配对（二维码 / 扫码 / 手动输入）+ 已配对设备（在线状态、自动重连、撤销授权）
 * + 发现中（未配对）设备。未配对设备禁止直接连接。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DevicesScreen(
    pairedDevices: List<PairedDevice>,
    peers: List<DiscoveredPeer>,
    connectedIds: Set<String>,
    linkStates: Map<String, ConnectionSupervisor.LinkState>,
    supervisor: ConnectionSupervisor,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val directory = remember { DeviceDirectory.getInstance(context) }

    var pairTarget by remember { mutableStateOf<DiscoveredPeer?>(null) }
    var revokeTarget by remember { mutableStateOf<PairedDevice?>(null) }
    var showQrDialog by remember { mutableStateOf(false) }
    var showManualDialog by remember { mutableStateOf(false) }
    var pairingBusy by remember { mutableStateOf(false) }
    var pairError by remember { mutableStateOf<String?>(null) }
    var pairSuccess by remember { mutableStateOf<String?>(null) }

    fun runPair(block: suspend () -> PairedDevice) {
        if (pairingBusy) return
        scope.launch {
            pairingBusy = true
            try {
                val device = block()
                pairSuccess = device.name
                val hasAccess = android.os.Build.VERSION.SDK_INT < 30 || android.os.Environment.isExternalStorageManager()
                if (hasAccess) runCatching {
                    androidx.core.content.ContextCompat.startForegroundService(context,
                        android.content.Intent(context, SftpServerService::class.java).apply { action = SftpServerService.ACTION_START })
                }

            } catch (e: PairingException) {
                pairError = e.message
            } catch (e: Exception) {
                pairError = "配对失败：${e.message ?: e.javaClass.simpleName}"
            } finally {
                pairingBusy = false
            }
        }
    }

    val scanLauncher = rememberLauncherForActivityResult(ScanContract()) { result ->
        val contents = result.contents
        if (contents.isNullOrBlank()) {
            pairError = "未识别到二维码内容，请对准二维码后重试"
        } else {
            runPair { PairingClient.joinByQr(context, contents) }
        }
    }

    val pairedIds = pairedDevices.map { it.deviceId }.toSet()
    val unpaired = peers.filter { it.deviceId !in pairedIds }

    Scaffold(
        topBar = { TopAppBar(title = { Text("设备") }) },
        contentWindowInsets = WindowInsets(0, 0, 0, 0)
    ) { padding ->
        LazyColumn(
            modifier = Modifier
                .padding(padding)
                .fillMaxSize()
                .padding(horizontal = 12.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            item {
                PairingActionsCard(
                    busy = pairingBusy,
                    onShowQr = { showQrDialog = true },
                    onScan = {
                        scanLauncher.launch(
                            ScanOptions().apply {
                                setDesiredBarcodeFormats(ScanOptions.QR_CODE)
                                setPrompt("对准对方设备上的配对二维码")
                                setBeepEnabled(false)
                                setOrientationLocked(false)
                            }
                        )
                    },
                    onManual = { showManualDialog = true },
                )
            }

            item {
                Text(
                    "配对是双向信任：本机记录对方 Host Key 指纹；配对成功后双方自动交换客户端公钥。" +
                        "已配对设备在局域网内上线时会自动重连（设置页可关闭）。",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(vertical = 4.dp)
                )
            }

            item {
                Text(
                    "已配对设备 (${pairedDevices.size})",
                    style = MaterialTheme.typography.titleSmall,
                    modifier = Modifier.padding(top = 4.dp)
                )
            }

            if (pairedDevices.isEmpty()) {
                item {
                    Text(
                        "还没有配对设备。请使用上方二维码配对，或从下方\"发现中\"选择设备完成配对。",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }

            items(items = pairedDevices, key = { it.deviceId }) { device ->
                val discovered = peers.firstOrNull { it.deviceId == device.deviceId }
                val link = linkStates[device.deviceId]
                val discoveredConflict = discovered != null &&
                    discovered.fingerprint.isNotBlank() &&
                    discovered.fingerprint != device.hostKeyFingerprint
                val conflict = discoveredConflict || link?.conflict == true
                PairedDeviceCard(
                    device = device,
                    online = discovered != null && !conflict,
                    conflict = conflict,
                    connected = device.deviceId in connectedIds,
                    link = link,
                    onConnect = {
                        supervisor.clearSuppression(device.deviceId)
                        scope.launch {
                            try {
                                ProviderHub.providerFor(device.deviceId)
                                Toast.makeText(context, "已连接 ${device.name}", Toast.LENGTH_SHORT).show()
                            } catch (e: Exception) {
                                Toast.makeText(
                                    context,
                                    "连接失败：${e.message}",
                                    Toast.LENGTH_LONG
                                ).show()
                            }
                        }
                    },
                    onDisconnect = { supervisor.disconnectDevice(device.deviceId) },
                    onRevoke = { revokeTarget = device },
                )
            }

            item {
                Spacer(Modifier.height(8.dp))
                Text(
                    "发现中 (${unpaired.size})",
                    style = MaterialTheme.typography.titleSmall,
                    modifier = Modifier.padding(top = 4.dp)
                )
            }

            if (unpaired.isEmpty()) {
                item {
                    Text(
                        "未发现其他设备。请确认对方已启动\"文件服务\"；若处于访客网络或路由器开启了 AP 隔离，" +
                            "设备之间无法通过 mDNS 发现。",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }

            items(items = unpaired, key = { it.deviceId }) { peer ->
                DiscoveredPeerCard(peer = peer, onPair = { pairTarget = peer })
            }

            item { Spacer(Modifier.height(24.dp)) }
        }
    }

    if (showQrDialog) {
        PairingQrDialog(onDismiss = { showQrDialog = false })
    }

    if (showManualDialog) {
        ManualPairDialog(
            busy = pairingBusy,
            onDismiss = { showManualDialog = false },
            onConfirm = { ip, port, code, fingerprint ->
                showManualDialog = false
                runPair { PairingClient.joinManually(context, ip, port, code, fingerprint) }
            }
        )
    }

    pairError?.let { message ->
        AlertDialog(
        icon = { BrandDialogIcon() },
            onDismissRequest = { pairError = null },
            title = { Text("配对失败") },
            text = { Text(message) },
            confirmButton = { TextButton(onClick = { pairError = null }) { Text("知道了") } }
        )
    }

    pairSuccess?.let { name ->
        AlertDialog(
        icon = { BrandDialogIcon() },
            onDismissRequest = { pairSuccess = null },
            title = { Text("配对成功") },
            text = { Text("已与「$name」保存配对身份，之后由程序自动验证。若电脑仍未连接，请在设置中授予共享存储权限并启动文件服务。") },
            confirmButton = { TextButton(onClick = { pairSuccess = null }) { Text("完成") } }
        )
    }

    pairTarget?.let { peer ->
        ManualPairDialog(
            busy = pairingBusy,
            initialIp = peer.host,
            initialFingerprint = peer.fingerprint,
            onDismiss = { pairTarget = null },
            onConfirm = { ip, port, code, fingerprint ->
                pairTarget = null
                runPair { PairingClient.joinManually(context, ip, port, code, fingerprint) }
            }
        )
    }

    revokeTarget?.let { device ->
        AlertDialog(
        icon = { BrandDialogIcon() },
            onDismissRequest = { revokeTarget = null },
            title = { Text("撤销授权") },
            text = {
                Text(
                    "将删除与「${device.name}」的配对记录，断开当前连接，并从本机 authorized_keys " +
                        "移除此设备公钥（若曾记录）。对方后续连接将被拒绝。"
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    revokeTarget = null
                    supervisor.disconnectDevice(device.deviceId)
                    directory.remove(device.deviceId)
                    device.peerKeyBlob?.let { blob ->
                        AuthorizedKeysStore(context).remove(blob)
                    }
                    Toast.makeText(context, "已撤销配对", Toast.LENGTH_SHORT).show()
                }) { Text("撤销") }
            },
            dismissButton = {
                TextButton(onClick = { revokeTarget = null }) { Text("取消") }
            }
        )
    }
}

@Composable
private fun PairingActionsCard(
    busy: Boolean,
    onShowQr: () -> Unit,
    onScan: () -> Unit,
    onManual: () -> Unit,
) {
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("添加设备", style = MaterialTheme.typography.titleMedium)
            Text(
                "同一局域网内，一台设备显示二维码，另一台扫码即可完成双向配对。",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
            Button(onClick = onShowQr, enabled = !busy, modifier = Modifier.fillMaxWidth()) {
                Text("配对这台设备（显示二维码）")
            }
            OutlinedButton(onClick = onScan, enabled = !busy, modifier = Modifier.fillMaxWidth()) {
                Text("扫码添加设备")
            }
            TextButton(onClick = onManual, enabled = !busy) {
                Text("扫码不可用？手动输入 IP + 配对码")
            }
            if (busy) {
                LinearProgressIndicator(Modifier.fillMaxWidth())
                Text("正在与对方设备配对…", style = MaterialTheme.typography.bodySmall)
            }
        }
    }
}

@Composable
private fun PairedDeviceCard(
    device: PairedDevice,
    online: Boolean,
    conflict: Boolean,
    connected: Boolean,
    link: ConnectionSupervisor.LinkState?,
    onConnect: () -> Unit,
    onDisconnect: () -> Unit,
    onRevoke: () -> Unit,
) {
    val retrySeconds = link?.nextRetryInSeconds ?: -1
    val statusText = when {
        device.type == "windows" -> "已授权电脑"
        conflict -> "身份冲突 · 禁止连接"
        connected -> "已连接"
        link?.authFailed == true -> "认证失败 · 已停止重连"
        retrySeconds > 0 -> "重试中（${retrySeconds}s）"
        online -> "在线"
        else -> "离线"
    }
    val statusColor = when {
        conflict || link?.authFailed == true -> MaterialTheme.colorScheme.error
        connected || online -> MaterialTheme.colorScheme.primary
        else -> MaterialTheme.colorScheme.onSurfaceVariant
    }

    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Row {
                Column(Modifier.weight(1f)) {
                    Text(device.name, style = MaterialTheme.typography.titleMedium)
                    Text(
                        device.deviceId,
                        fontFamily = FontFamily.Monospace,
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                }
                Text(statusText, style = MaterialTheme.typography.labelMedium, color = statusColor)
            }
            Text("类型：${device.type}", style = MaterialTheme.typography.bodySmall)
            Text(
                "最近地址：${device.lastKnownIp ?: "未知"}:${device.lastKnownPort}",
                style = MaterialTheme.typography.bodySmall
            )
            Text(
                "Host Key 指纹",
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
            Text(
                device.hostKeyFingerprint,
                fontFamily = FontFamily.Monospace,
                style = MaterialTheme.typography.labelSmall,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
            if (conflict) {
                Text(
                    "发现设备的指纹与配对记录不一致，可能是设备重装/密钥更换或中间人攻击。" +
                        "请撤销配对后核对指纹重新配对。",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.error
                )
            } else if (link?.authFailed == true) {
                Text(
                    "对方拒绝了本机公钥（SFTP 认证失败），自动重连已停止。请检查对方 authorized_keys 或重新配对。",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.error
                )
            } else if (retrySeconds > 0) {
                Text(
                    "连接失败：${link?.lastError ?: "未知原因"}，将在 ${retrySeconds} 秒后重试。",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.error
                )
            }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                if (device.type == "windows") {
                    Text("电脑会连接本机共享服务", style = MaterialTheme.typography.bodySmall)
                } else if (connected) {
                    OutlinedButton(onClick = onDisconnect) { Text("断开") }
                } else {
                    OutlinedButton(onClick = onConnect, enabled = !conflict) { Text("连接") }
                }
                TextButton(onClick = onRevoke) { Text("撤销授权") }
            }
        }
    }
}

@Composable
private fun DiscoveredPeerCard(peer: DiscoveredPeer, onPair: () -> Unit) {
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Row {
                Column(Modifier.weight(1f)) {
                    Text(peer.name, style = MaterialTheme.typography.titleMedium)
                    Text(
                        "${peer.host}:${peer.port} · ${peer.type}",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
                Text("未配对", style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.tertiary)
            }
            Text(
                if (peer.fingerprint.isBlank()) "未广播 Host Key 指纹（禁止配对）"
                else "指纹：${peer.fingerprint}",
                fontFamily = FontFamily.Monospace,
                style = MaterialTheme.typography.labelSmall,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis
            )
            OutlinedButton(onClick = onPair) { Text("配对…") }
        }
    }
}

/**
 * 二维码配对窗口：全屏展示二维码 + 6 位配对码 + IP:47821 + 倒计时。
 * 关闭对话框即停止临时 HTTP 服务。
 */
@Composable
private fun PairingQrDialog(onDismiss: () -> Unit) {
    val context = LocalContext.current
    var generation by remember { mutableStateOf(0) }
    val session = remember(generation) { PairingSession() }
    val server = remember(generation) { PairingServer(context, session) }
    val serverStatus by server.status.collectAsState()
    val sessionState by session.state.collectAsState()

    var bitmap by remember(generation) { mutableStateOf<Bitmap?>(null) }
    var qrError by remember(generation) { mutableStateOf<String?>(null) }

    DisposableEffect(server) {
        server.start()
        onDispose {
            server.close()
            session.close()
        }
    }

    LaunchedEffect(generation, serverStatus.ip) {
        val ip = serverStatus.ip ?: return@LaunchedEffect
        val rendered = withContext(Dispatchers.IO) {
            runCatching {
                val hostKey = DeviceIdentity.getOrCreateHostKeyPair()
                val rsaPublic = hostKey.public as RSAPublicKey
                val payload = PairingPayload(
                    ip = ip,
                    port = serverStatus.port,
                    code = session.code,
                    deviceId = DeviceIdentity.getDeviceId(context),
                    name = Build.MODEL,
                    type = "phone",
                    fingerprint = DeviceIdentity.fingerprintSha256(rsaPublic),
                )
                PairingQr.render(payload.toJson(), 720)
            }.getOrNull()
        }
        bitmap = rendered
        qrError = if (rendered == null) "无法生成二维码，请确认设备密钥可用后重试" else null
    }

    Dialog(
        onDismissRequest = onDismiss,
        properties = DialogProperties(usePlatformDefaultWidth = false)
    ) {
        Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.surface) {
            Column(
                modifier = Modifier
                    .fillMaxSize()
                    .verticalScroll(rememberScrollState())
                    .padding(24.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                BrandDialogIcon()
                Text("配对这台设备", style = MaterialTheme.typography.titleLarge)
                Text(
                    "在另一台设备上打开 PhoneDrive → 设备 → 扫码添加设备，扫描下方二维码；" +
                        "扫码不可用时，请在对方输入地址、配对码和下方完整指纹。",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )

                val serverError = serverStatus.error
                if (serverError != null) {
                    Text(serverError, color = MaterialTheme.colorScheme.error,
                        style = MaterialTheme.typography.bodySmall)
                    OutlinedButton(onClick = { generation++ }) { Text("重试") }
                } else {
                    when {
                        qrError != null -> Text(qrError!!, color = MaterialTheme.colorScheme.error)
                        bitmap == null -> Text("正在生成二维码…", style = MaterialTheme.typography.bodySmall)
                        else -> Image(
                            bitmap = bitmap!!.asImageBitmap(),
                            contentDescription = "配对二维码",
                            modifier = Modifier.size(260.dp),
                            contentScale = ContentScale.Fit,
                        )
                    }

                    Text(
                        sessionState.code,
                        fontFamily = FontFamily.Monospace,
                        style = MaterialTheme.typography.displaySmall,
                        letterSpacing = 8.sp
                    )
                    Text(
                        "${serverStatus.ip ?: "未知 IP"} : ${serverStatus.port}",
                        fontFamily = FontFamily.Monospace,
                        style = MaterialTheme.typography.bodyMedium
                    )
                    Text(DeviceIdentity.fingerprintSha256(DeviceIdentity.getOrCreateHostKeyPair().public as RSAPublicKey),
                        fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.labelSmall)
                    if (sessionState.approvalPending) {
                        Text("允许 ${sessionState.peerName} 访问本机共享目录？", style = MaterialTheme.typography.titleMedium)
                        Text(sessionState.peerFingerprint ?: "", fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.labelSmall)
                        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                            Button(onClick = { session.approvePeer(true) }) { Text("允许并配对") }
                            OutlinedButton(onClick = { session.approvePeer(false) }) { Text("拒绝") }
                        }
                    }
                    when (sessionState.state) {
                        PairingSession.State.PENDING -> Text(
                            "配对窗口剩余 ${sessionState.remainingSeconds} 秒",
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.primary
                        )
                        PairingSession.State.SUCCESS -> Text(
                            "配对成功：${sessionState.peerName ?: "对端设备"}",
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.primary
                        )
                        PairingSession.State.EXPIRED, PairingSession.State.CANCELED -> Text(
                            sessionState.message ?: "配对窗口已关闭",
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.error
                        )
                        PairingSession.State.IDLE -> Text(
                            "等待服务启动…",
                            style = MaterialTheme.typography.bodySmall
                        )
                    }
                    if (sessionState.state == PairingSession.State.PENDING && sessionState.failedAttempts > 0) {
                        Text(
                            "配对码错误次数：${sessionState.failedAttempts}/${PairingSession.MAX_FAILED_ATTEMPTS}",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.error
                        )
                    }
                }

                Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    if (sessionState.state == PairingSession.State.EXPIRED) {
                        OutlinedButton(onClick = { generation++ }) { Text("重新生成") }
                    }
                    Button(onClick = onDismiss) { Text("关闭") }
                }
                Spacer(Modifier.height(24.dp))
            }
        }
    }
}

@Composable
private fun ManualPairDialog(
    busy: Boolean,
    onDismiss: () -> Unit,
    onConfirm: (String, Int, String, String) -> Unit,
    initialIp: String = "",
    initialFingerprint: String = "",
) {
    var ip by remember { mutableStateOf(initialIp) }
    var fingerprint by remember { mutableStateOf(initialFingerprint) }
    var port by remember { mutableStateOf("47821") }
    var code by remember { mutableStateOf("") }
    val portValue = port.toIntOrNull()
    val valid = ip.trim().isNotEmpty() &&
        portValue != null && portValue in 1..65535 &&
        code.trim().length == 6 && code.trim().all { it.isDigit() } && fingerprint.trim().startsWith("SHA256:")

    AlertDialog(
        icon = { BrandDialogIcon() },
        onDismissRequest = onDismiss,
        title = { Text("手动输入配对信息") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(
                    "请输入对方配对页的地址和配对码，并逐字核对完整指纹。对方还需确认本次请求。",
                    style = MaterialTheme.typography.bodySmall
                )
                OutlinedTextField(
                    value = ip,
                    onValueChange = { ip = it },
                    label = { Text("对方 IP（如 192.168.1.10）") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri)
                )
                OutlinedTextField(
                    value = port,
                    onValueChange = { port = it.filter(Char::isDigit).take(5) },
                    label = { Text("端口（默认 47821）") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number)
                )
                OutlinedTextField(
                    value = code,
                    onValueChange = { code = it.filter(Char::isDigit).take(6) },
                    label = { Text("6 位配对码") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number)
                )
                OutlinedTextField(value = fingerprint, onValueChange = { fingerprint = it },
                    label = { Text("对端 Host Key 指纹（SHA256:…）") }, modifier = Modifier.fillMaxWidth())
                if (busy) Text("等待对方确认…", style = MaterialTheme.typography.bodySmall)
            }
        },
        confirmButton = {
            TextButton(
                onClick = { onConfirm(ip.trim(), portValue ?: 47821, code.trim(), fingerprint.trim()) },
                enabled = valid && !busy
            ) { Text("开始配对") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("取消") } }
    )
}

@Composable
private fun PairingDialog(
    peer: DiscoveredPeer,
    alreadyPaired: Boolean,
    onDismiss: () -> Unit,
    onConfirm: () -> Unit,
) {
    val context = LocalContext.current
    var clientKeyLine by remember { mutableStateOf<String?>(null) }
    var loading by remember { mutableStateOf(true) }

    LaunchedEffect(peer.deviceId) {
        val line = withContext(Dispatchers.IO) {
            runCatching {
                ClientKey.getOrCreateKeyPair()
                ClientKey.openSshPublicKeyLine()
            }.getOrNull()
        }
        clientKeyLine = line
        loading = false
    }

    val pairable = peer.fingerprint.isNotBlank() && !alreadyPaired

    AlertDialog(
        icon = { BrandDialogIcon() },
        onDismissRequest = onDismiss,
        title = { Text("与「${peer.name}」配对") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("1. 请与对方设备设置页显示的 Host Key 指纹逐字核对：",
                    style = MaterialTheme.typography.bodySmall)
                Text(
                    peer.fingerprint.ifBlank { "（对方未广播指纹）" },
                    fontFamily = FontFamily.Monospace,
                    style = MaterialTheme.typography.labelSmall,
                    color = if (peer.fingerprint.isBlank()) MaterialTheme.colorScheme.error
                    else MaterialTheme.colorScheme.onSurface
                )
                Text("2. 在对方设备：设置 → 添加公钥，粘贴本机客户端公钥。",
                    style = MaterialTheme.typography.bodySmall)
                when {
                    loading -> Text("正在读取本机客户端公钥…", style = MaterialTheme.typography.bodySmall)
                    clientKeyLine == null -> Text(
                        "客户端公钥不可用，请重启应用后重试。",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.error
                    )
                    else -> {
                        Text(
                            clientKeyLine ?: "",
                            fontFamily = FontFamily.Monospace,
                            style = MaterialTheme.typography.labelSmall,
                            maxLines = 5
                        )
                        TextButton(onClick = {
                            val line = clientKeyLine ?: return@TextButton
                            val manager = context.getSystemService(Context.CLIPBOARD_SERVICE)
                                as ClipboardManager
                            manager.setPrimaryClip(
                                ClipData.newPlainText("PhoneDrive 客户端公钥", line)
                            )
                            Toast.makeText(context, "已复制", Toast.LENGTH_SHORT).show()
                        }) { Text("复制公钥") }
                    }
                }
                if (alreadyPaired) {
                    Text("该设备已在配对列表中。", color = MaterialTheme.colorScheme.error,
                        style = MaterialTheme.typography.bodySmall)
                }
                Text("3. 确认无误后保存配对（本机只保存指纹，不保存密码）。",
                    style = MaterialTheme.typography.bodySmall)
            }
        },
        confirmButton = {
            TextButton(onClick = onConfirm, enabled = pairable) {
                Text("已核对，保存配对")
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("取消") } }
    )
}
