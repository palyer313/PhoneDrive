package com.phonedrive.android.ui

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Devices
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Star
import androidx.compose.material.icons.filled.SwapVert
import androidx.compose.material3.Icon
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import com.phonedrive.android.filemanager.ProviderHub
import com.phonedrive.android.remote.ConnectionSupervisor
import com.phonedrive.android.remote.DeviceDirectory
import com.phonedrive.android.remote.PeerDiscovery

/**
 * 应用主界面：底部导航（文件 / 设备 / 传输 / 收藏 / 设置）。
 * 文件页双栏与多标签状态在此提升，切换底部页签不会丢失浏览位置。
 */
@Composable
fun PhoneDriveRoot() {
    val context = LocalContext.current

    var currentTab by rememberSaveable { mutableStateOf(0) }
    val paneA = rememberFilePaneState()
    val paneB = rememberFilePaneState()
    var secondPaneVisible by rememberSaveable { mutableStateOf(false) }

    val discovery = remember { PeerDiscovery(context) }
    val supervisor = remember { ConnectionSupervisor(context, discovery) }
    DisposableEffect(Unit) {
        discovery.start()
        supervisor.start()
        onDispose {
            supervisor.stop()
            discovery.stop()
        }
    }

    val peers by discovery.peers.collectAsState()
    val pairedDevices by remember { DeviceDirectory.getInstance(context) }.devices.collectAsState()
    val connectedIds by ProviderHub.connected.collectAsState()
    val linkStates by supervisor.links.collectAsState()

    Scaffold(
        bottomBar = {
            NavigationBar {
                NavigationBarItem(
                    selected = currentTab == 0,
                    onClick = { currentTab = 0 },
                    icon = { Icon(Icons.Default.Folder, contentDescription = null) },
                    label = { Text("文件") }
                )
                NavigationBarItem(
                    selected = currentTab == 1,
                    onClick = { currentTab = 1 },
                    icon = { Icon(Icons.Default.Devices, contentDescription = null) },
                    label = { Text("设备") }
                )
                NavigationBarItem(
                    selected = currentTab == 2,
                    onClick = { currentTab = 2 },
                    icon = { Icon(Icons.Default.SwapVert, contentDescription = null) },
                    label = { Text("传输") }
                )
                NavigationBarItem(
                    selected = currentTab == 3,
                    onClick = { currentTab = 3 },
                    icon = { Icon(Icons.Default.Star, contentDescription = null) },
                    label = { Text("收藏") }
                )
                NavigationBarItem(
                    selected = currentTab == 4,
                    onClick = { currentTab = 4 },
                    icon = { Icon(Icons.Default.Settings, contentDescription = null) },
                    label = { Text("设置") }
                )
            }
        }
    ) { padding ->
        Box(Modifier.fillMaxSize().padding(padding)) {
            when (currentTab) {
                0 -> FilesScreen(
                    paneA = paneA,
                    paneB = paneB,
                    secondPaneVisible = secondPaneVisible,
                    onToggleSecondPane = { secondPaneVisible = it },
                    pairedDevices = pairedDevices,
                    peers = peers,
                )
                1 -> DevicesScreen(
                    pairedDevices = pairedDevices,
                    peers = peers,
                    connectedIds = connectedIds,
                    linkStates = linkStates,
                    supervisor = supervisor,
                )
                2 -> TransfersScreen()
                3 -> FavoritesScreen(
                    onOpen = { location ->
                        paneA.navigate(location)
                        currentTab = 0
                    }
                )
                else -> SettingsScreen(
                    onAutoReconnectChanged = { supervisor.refresh() },
                )
            }
        }
    }
}
