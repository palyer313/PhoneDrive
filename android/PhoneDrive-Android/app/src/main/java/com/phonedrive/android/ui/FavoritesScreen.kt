package com.phonedrive.android.ui

import androidx.compose.foundation.clickable
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
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Star
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.phonedrive.android.filemanager.Bookmark
import com.phonedrive.android.filemanager.BookmarkStore
import com.phonedrive.android.filemanager.RecentLocation
import com.phonedrive.android.filemanager.StorageLocation
import com.phonedrive.android.remote.DeviceDirectory
import com.phonedrive.android.remote.PairedDevice

/**
 * 收藏 / 历史页：收藏的目录与最近访问路径，点击可回到文件页对应位置。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun FavoritesScreen(onOpen: (StorageLocation) -> Unit) {
    val context = LocalContext.current
    val store = remember { BookmarkStore.getInstance(context) }
    val favorites by store.favorites.collectAsState()
    val history by store.history.collectAsState()
    val devDir = remember { DeviceDirectory.getInstance(context) }
    val devices by devDir.devices.collectAsState()

    Scaffold(
        topBar = { TopAppBar(title = { Text("收藏") }) },
        contentWindowInsets = WindowInsets(0, 0, 0, 0)
    ) { padding ->
        LazyColumn(
            modifier = Modifier.padding(padding).fillMaxSize().padding(horizontal = 12.dp),
            verticalArrangement = Arrangement.spacedBy(2.dp)
        ) {
            item {
                Text("收藏的目录 (${favorites.size})",
                    style = MaterialTheme.typography.titleSmall,
                    modifier = Modifier.padding(top = 12.dp, bottom = 4.dp))
            }
            if (favorites.isEmpty()) {
                item {
                    Text(
                        "暂无收藏。可在文件页菜单\"收藏当前目录\"，或在文件属性中收藏。",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(bottom = 8.dp)
                    )
                }
            }
            items(items = favorites, key = { "${it.deviceId}:${it.path}" }) { favorite ->
                FavoriteRow(
                    favorite = favorite,
                    deviceName = deviceLabel(favorite.deviceId, devices),
                    onClick = { onOpen(favorite.location) },
                    onRemove = { store.removeFavorite(favorite.deviceId, favorite.path) }
                )
            }

            item {
                Row(
                    Modifier.fillMaxWidth().padding(top = 16.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text("最近访问 (${history.size})",
                        style = MaterialTheme.typography.titleSmall, modifier = Modifier.weight(1f))
                    if (history.isNotEmpty()) {
                        TextButton(onClick = { store.clearHistory() }) { Text("清空") }
                    }
                }
            }
            if (history.isEmpty()) {
                item {
                    Text(
                        "暂无访问记录。",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }
            items(items = history, key = { "${it.deviceId}:${it.path}" }) { recent ->
                RecentRow(
                    recent = recent,
                    deviceName = deviceLabel(recent.deviceId, devices),
                    onClick = { onOpen(recent.location) },
                    onRemove = { store.removeHistory(recent.deviceId, recent.path) }
                )
            }
            item { Spacer(Modifier.height(24.dp)) }
        }
    }
}

@Composable
private fun FavoriteRow(
    favorite: Bookmark,
    deviceName: String,
    onClick: () -> Unit,
    onRemove: () -> Unit,
) {
    Row(
        Modifier.fillMaxWidth().clickable(onClick = onClick).padding(vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Icon(Icons.Default.Star, contentDescription = null,
            tint = MaterialTheme.colorScheme.primary)
        Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
            Text(
                if (favorite.name == "/") "根目录" else favorite.name,
                style = MaterialTheme.typography.bodyMedium,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
            Text("$deviceName · ${favorite.path}",
                fontFamily = FontFamily.Monospace,
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis)
        }
        IconButton(onClick = onRemove) {
            Icon(Icons.Default.Close, contentDescription = "移除收藏")
        }
    }
}

@Composable
private fun RecentRow(
    recent: RecentLocation,
    deviceName: String,
    onClick: () -> Unit,
    onRemove: () -> Unit,
) {
    Row(
        Modifier.fillMaxWidth().clickable(onClick = onClick).padding(vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Icon(Icons.Default.Folder, contentDescription = null,
            tint = MaterialTheme.colorScheme.onSurfaceVariant)
        Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
            Text(
                if (recent.name == "/") "根目录" else recent.name,
                style = MaterialTheme.typography.bodyMedium,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
            Text("$deviceName · ${recent.path}",
                fontFamily = FontFamily.Monospace,
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis)
        }
        IconButton(onClick = onRemove) {
            Icon(Icons.Default.Close, contentDescription = "移除记录")
        }
    }
}

private fun deviceLabel(deviceId: String?, devices: List<PairedDevice>): String =
    if (deviceId == null) "本地存储"
    else devices.firstOrNull { it.deviceId == deviceId }?.name ?: "未知设备"
