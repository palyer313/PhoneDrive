package com.phonedrive.android.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.Card
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.phonedrive.android.remote.DeviceDirectory
import com.phonedrive.android.remote.PairedDevice
import com.phonedrive.android.transfer.TransferEngine
import com.phonedrive.android.transfer.TransferState
import com.phonedrive.android.transfer.TransferTask
import com.phonedrive.android.transfer.TransferType

/**
 * 传输页：任务列表、进度、速度/剩余时间、暂停/恢复/取消/重试。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TransfersScreen() {
    val context = LocalContext.current
    val engine = remember { TransferEngine.get(context) }
    val tasks by engine.tasks.collectAsState()
    val devices by remember { DeviceDirectory.getInstance(context) }.devices.collectAsState()

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("传输") },
                actions = {
                    TextButton(onClick = { engine.clearFinished() }) { Text("清除已完成") }
                }
            )
        },
        contentWindowInsets = WindowInsets(0, 0, 0, 0)
    ) { padding ->
        if (tasks.isEmpty()) {
            Column(
                Modifier.padding(padding).fillMaxSize().padding(24.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                Text("暂无传输任务", style = MaterialTheme.typography.titleMedium)
                Text(
                    "在文件页复制/剪切文件后，切换到目标目录粘贴即可开始传输。",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        } else {
            LazyColumn(
                modifier = Modifier.padding(padding).fillMaxSize().padding(horizontal = 12.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                items(
                    items = tasks.sortedByDescending { it.createdAt },
                    key = { it.taskId }
                ) { task ->
                    TransferCard(
                        task = task,
                        devices = devices,
                        onPause = { engine.pause(task.taskId) },
                        onResume = { engine.resume(task.taskId) },
                        onCancel = { engine.cancel(task.taskId) },
                        onRemove = { engine.remove(task.taskId) },
                    )
                }
            }
        }
    }
}

@Composable
private fun TransferCard(
    task: TransferTask,
    devices: List<PairedDevice>,
    onPause: () -> Unit,
    onResume: () -> Unit,
    onCancel: () -> Unit,
    onRemove: () -> Unit,
) {
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Row {
                Column(Modifier.weight(1f)) {
                    Text(
                        task.displayName,
                        style = MaterialTheme.typography.titleSmall,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                    Text(
                        (if (task.type == TransferType.MOVE) "移动" else "复制") +
                            " · ${deviceLabel(task.srcDeviceId, devices)} → ${deviceLabel(task.dstDeviceId, devices)}",
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
                Text(
                    stateLabel(task.state),
                    style = MaterialTheme.typography.labelMedium,
                    color = stateColor(task.state)
                )
            }

            Text(
                "源：${task.srcPath}",
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
            Text(
                "目标：${task.dstPath}",
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )

            LinearProgressIndicator(
                progress = { task.progress },
                modifier = Modifier.fillMaxWidth()
            )

            Row {
                Text(
                    "${formatSize(task.transferred)} / ${formatSize(task.size)}" +
                        " · ${(task.progress * 100).toInt()}%",
                    style = MaterialTheme.typography.labelSmall
                )
                Spacer(Modifier.weight(1f))
                if (task.state == TransferState.RUNNING) {
                    Text(
                        "${formatSpeed(task.speedBps)} · 剩余 ${formatEta(task.etaSeconds)}",
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }

            if (task.error != null) {
                Text(
                    task.error ?: "",
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.error
                )
            }

            Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                when (task.state) {
                    TransferState.RUNNING, TransferState.VERIFYING, TransferState.QUEUED -> {
                        TextButton(onClick = onPause) { Text("暂停") }
                        TextButton(onClick = onCancel) { Text("取消") }
                    }
                    TransferState.PAUSED -> {
                        TextButton(onClick = onResume) { Text("继续") }
                        TextButton(onClick = onCancel) { Text("取消") }
                    }
                    TransferState.FAILED -> {
                        TextButton(onClick = onResume) { Text("重试") }
                        TextButton(onClick = onRemove) { Text("删除") }
                    }
                    else -> {
                        TextButton(onClick = onRemove) { Text("删除任务") }
                    }
                }
            }
        }
    }
}

private fun deviceLabel(deviceId: String?, devices: List<PairedDevice>): String =
    if (deviceId == null) "本机" else devices.firstOrNull { it.deviceId == deviceId }?.name ?: "未知设备"

private fun stateLabel(state: TransferState): String = when (state) {
    TransferState.QUEUED -> "排队中"
    TransferState.RUNNING -> "传输中"
    TransferState.PAUSED -> "已暂停"
    TransferState.VERIFYING -> "校验中"
    TransferState.DONE -> "完成"
    TransferState.FAILED -> "失败"
    TransferState.CANCELED -> "已取消"
    TransferState.SKIPPED -> "已跳过"
}

@Composable
private fun stateColor(state: TransferState): androidx.compose.ui.graphics.Color = when (state) {
    TransferState.RUNNING, TransferState.VERIFYING -> MaterialTheme.colorScheme.primary
    TransferState.DONE -> MaterialTheme.colorScheme.primary
    TransferState.FAILED -> MaterialTheme.colorScheme.error
    TransferState.CANCELED, TransferState.SKIPPED -> MaterialTheme.colorScheme.onSurfaceVariant
    else -> MaterialTheme.colorScheme.onSurfaceVariant
}
