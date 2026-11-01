package com.phonedrive.android.ui

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.widget.Toast
import androidx.compose.foundation.Image as ComposeImage
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.ArrowUpward
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material.icons.filled.ContentCut
import androidx.compose.material.icons.filled.CreateNewFolder
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.FilterList
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Image
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.InsertDriveFile
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Star
import androidx.compose.material.icons.filled.StarBorder
import androidx.compose.material.icons.filled.SwapVert
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Checkbox
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.VerticalDivider
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.phonedrive.android.filemanager.BookmarkStore
import com.phonedrive.android.filemanager.FileEntry
import com.phonedrive.android.filemanager.FileIconKind
import com.phonedrive.android.filemanager.FileProvider
import com.phonedrive.android.filemanager.ProviderHub
import com.phonedrive.android.filemanager.StorageLocation
import com.phonedrive.android.remote.DiscoveredPeer
import com.phonedrive.android.remote.PairedDevice
import com.phonedrive.android.transfer.ConflictPolicy
import com.phonedrive.android.transfer.TransferEngine
import com.phonedrive.android.transfer.TransferRequest
import com.phonedrive.android.transfer.TransferType
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.util.UUID

enum class ClipMode { NONE, COPY, MOVE }

enum class FileSort(val label: String) {
    NAME("名称"), SIZE("大小"), TIME("时间"), TYPE("类型")
}

enum class FileFilter(val label: String) {
    ALL("全部"), FOLDER("文件夹"), FILE("文件"), IMAGE("图片"),
    VIDEO("视频"), AUDIO("音频"), DOCUMENT("文档"), ARCHIVE("压缩包")
}

/** 进程内剪贴板（复制/剪切），只保存引用，不复制内容 */
object FileClipboard {
    var mode by mutableStateOf(ClipMode.NONE)
    var deviceId by mutableStateOf<String?>(null)
    var entries by mutableStateOf<List<FileEntry>>(emptyList())

    fun put(newMode: ClipMode, newDeviceId: String?, newEntries: List<FileEntry>) {
        mode = newMode
        deviceId = newDeviceId
        entries = newEntries
    }

    fun clear() {
        mode = ClipMode.NONE
        deviceId = null
        entries = emptyList()
    }

    val hasContent: Boolean get() = entries.isNotEmpty() && mode != ClipMode.NONE
}

class FileTabState(
    deviceId: String?,
    path: String,
) {
    val tabId: String = UUID.randomUUID().toString()
    var deviceId by mutableStateOf(deviceId)
    var path by mutableStateOf(path)
    var entries by mutableStateOf<List<FileEntry>>(emptyList())
    var searchResults by mutableStateOf<List<FileEntry>?>(null)
    var loading by mutableStateOf(false)
    var error by mutableStateOf<String?>(null)
    var sortMode by mutableStateOf(FileSort.NAME)
    var sortDescending by mutableStateOf(false)
    var filter by mutableStateOf(FileFilter.ALL)
    var searchQuery by mutableStateOf("")
    var selectedPaths by mutableStateOf<Set<String>>(emptySet())
    var refreshToken by mutableStateOf(0)

    fun refresh() {
        refreshToken++
    }

    fun navigateTo(newDeviceId: String?, newPath: String) {
        deviceId = newDeviceId
        path = newPath
        selectedPaths = emptySet()
        searchQuery = ""
        searchResults = null
    }
}

class FilePaneState(startDeviceId: String? = null, startPath: String = "/") {

    val tabs = mutableStateListOf(FileTabState(startDeviceId, startPath))
    var selectedTab by mutableStateOf(0)

    val current: FileTabState
        get() = tabs[selectedTab.coerceIn(0, tabs.lastIndex)]

    fun newTab(deviceId: String? = current.deviceId, path: String = "/") {
        tabs.add(FileTabState(deviceId, path))
        selectedTab = tabs.lastIndex
    }

    fun closeTab(index: Int) {
        if (tabs.size <= 1) return
        tabs.removeAt(index)
        selectedTab = selectedTab.coerceIn(0, tabs.lastIndex)
    }

    fun navigate(location: StorageLocation) {
        current.navigateTo(location.deviceId, location.path)
    }
}

@Composable
fun rememberFilePaneState(): FilePaneState = remember { FilePaneState() }

private data class PendingPaste(
    val entries: List<FileEntry>,
    val sourceDeviceId: String?,
    val mode: ClipMode,
    val destinationDeviceId: String?,
    val destinationPath: String,
    val conflicts: List<String>,
)

@Composable
fun FilesScreen(
    paneA: FilePaneState,
    paneB: FilePaneState,
    secondPaneVisible: Boolean,
    onToggleSecondPane: (Boolean) -> Unit,
    pairedDevices: List<PairedDevice>,
    peers: List<DiscoveredPeer>,
) {
    BoxWithConstraints(
        Modifier.fillMaxSize().statusBarsPadding()
    ) {
        val wide = maxWidth >= 720.dp
        val showSecond = wide || secondPaneVisible
        when {
            wide -> Row(Modifier.fillMaxSize()) {
                FilePane(
                    pane = paneA,
                    modifier = Modifier.weight(1f).fillMaxSize(),
                    pairedDevices = pairedDevices.filter { it.type != "windows" },
                    peers = peers,
                    secondPaneVisible = true,
                    onShowSecondPane = null,
                )
                VerticalDivider()
                FilePane(
                    pane = paneB,
                    modifier = Modifier.weight(1f).fillMaxSize(),
                    pairedDevices = pairedDevices.filter { it.type != "windows" },
                    peers = peers,
                    secondPaneVisible = true,
                    onShowSecondPane = null,
                )
            }
            showSecond -> Column(Modifier.fillMaxSize()) {
                FilePane(
                    pane = paneA,
                    modifier = Modifier.weight(1f).fillMaxWidth(),
                    pairedDevices = pairedDevices.filter { it.type != "windows" },
                    peers = peers,
                    secondPaneVisible = true,
                    onShowSecondPane = { onToggleSecondPane(false) },
                )
                HorizontalDivider()
                FilePane(
                    pane = paneB,
                    modifier = Modifier.weight(1f).fillMaxWidth(),
                    pairedDevices = pairedDevices.filter { it.type != "windows" },
                    peers = peers,
                    secondPaneVisible = true,
                    onShowSecondPane = null,
                )
            }
            else -> FilePane(
                pane = paneA,
                modifier = Modifier.fillMaxSize(),
                pairedDevices = pairedDevices.filter { it.type != "windows" },
                peers = peers,
                secondPaneVisible = false,
                onShowSecondPane = { onToggleSecondPane(true) },
            )
        }
    }
}

@Composable
private fun FilePane(
    pane: FilePaneState,
    modifier: Modifier,
    pairedDevices: List<PairedDevice>,
    peers: List<DiscoveredPeer>,
    secondPaneVisible: Boolean,
    onShowSecondPane: (() -> Unit)?,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val bookmarkStore = remember { BookmarkStore.getInstance(context) }
    val tab = pane.current

    var searchActive by remember { mutableStateOf(false) }
    var showDeviceMenu by remember { mutableStateOf(false) }
    var showSortMenu by remember { mutableStateOf(false) }
    var showFilterMenu by remember { mutableStateOf(false) }
    var showMoreMenu by remember { mutableStateOf(false) }

    var renameTarget by remember { mutableStateOf<FileEntry?>(null) }
    var showNewFolder by remember { mutableStateOf(false) }
    var deleteTargets by remember { mutableStateOf<List<FileEntry>>(emptyList()) }
    var propertyTarget by remember { mutableStateOf<FileEntry?>(null) }
    var previewTarget by remember { mutableStateOf<FileEntry?>(null) }
    var pendingPaste by remember { mutableStateOf<PendingPaste?>(null) }

    val onlineDeviceIds = remember(peers) { peers.map { it.deviceId }.toSet() }

    // 目录加载
    LaunchedEffect(tab.tabId, tab.deviceId, tab.path, tab.refreshToken) {
        tab.loading = true
        tab.error = null
        try {
            val provider = ProviderHub.providerFor(tab.deviceId)
            val entries = withContext(Dispatchers.IO) { provider.list(tab.path) }
            tab.entries = entries
            bookmarkStore.recordVisit(
                tab.deviceId,
                tab.path,
                displayNameOf(tab.deviceId, pairedDevices)
            )
        } catch (e: Exception) {
            tab.error = e.message ?: e.javaClass.simpleName
        } finally {
            tab.loading = false
        }
    }

    // 搜索（防抖 350ms）
    LaunchedEffect(tab.tabId, tab.searchQuery, tab.deviceId, tab.path) {
        val query = tab.searchQuery.trim()
        if (query.isEmpty()) {
            tab.searchResults = null
            return@LaunchedEffect
        }
        delay(350)
        tab.loading = true
        try {
            val provider = ProviderHub.providerFor(tab.deviceId)
            tab.searchResults = withContext(Dispatchers.IO) { provider.search(query, tab.path) }
        } catch (e: Exception) {
            tab.error = e.message ?: e.javaClass.simpleName
        } finally {
            tab.loading = false
        }
    }

    val sourceList = tab.searchResults ?: tab.entries
    val visible = sourceList
        .filter { matchesFilter(it, tab.filter) }
        .sortedWith(entryComparator(tab.sortMode, tab.sortDescending))

    val selectionMode = tab.selectedPaths.isNotEmpty()
    val selectedEntries = sourceList.filter { it.path in tab.selectedPaths }

    fun refresh() = tab.refresh()

    fun toast(message: String) =
        Toast.makeText(context, message, Toast.LENGTH_SHORT).show()

    fun switchDevice(deviceId: String?) {
        scope.launch {
            tab.loading = true
            tab.error = null
            try {
                ProviderHub.providerFor(deviceId)
                tab.navigateTo(deviceId, "/")
                tab.refresh()
            } catch (e: Exception) {
                tab.error = e.message ?: "连接失败"
                toast(e.message ?: "连接失败")
            } finally {
                tab.loading = false
            }
        }
    }

    fun enqueuePaste(policy: ConflictPolicy) {
        val clipMode = FileClipboard.mode
        val entries = FileClipboard.entries
        val sourceDeviceId = FileClipboard.deviceId
        if (entries.isEmpty() || clipMode == ClipMode.NONE) return
        val requests = entries.map { entry ->
            TransferRequest(
                type = if (clipMode == ClipMode.MOVE) TransferType.MOVE else TransferType.COPY,
                srcDeviceId = sourceDeviceId,
                srcPath = entry.path,
                dstDeviceId = tab.deviceId,
                dstPath = FileProvider.join(tab.path, entry.name),
                size = entry.size,
                conflictPolicy = policy,
                srcModifiedAt = entry.modifiedAt,
            )
        }
        TransferEngine.get(context).enqueueAll(requests)
        if (clipMode == ClipMode.MOVE) FileClipboard.clear()
        toast(if (clipMode == ClipMode.MOVE) "已加入移动队列" else "已加入复制队列")
    }

    fun startPaste() {
        if (!FileClipboard.hasContent) return
        val entries = FileClipboard.entries
        scope.launch {
            try {
                val provider = ProviderHub.providerFor(tab.deviceId)
                val conflicts = withContext(Dispatchers.IO) {
                    entries.filter { provider.exists(FileProvider.join(tab.path, it.name)) }
                        .map { it.name }
                }
                if (conflicts.isEmpty()) {
                    enqueuePaste(ConflictPolicy.ASK)
                } else {
                    pendingPaste = PendingPaste(
                        entries = entries,
                        sourceDeviceId = FileClipboard.deviceId,
                        mode = FileClipboard.mode,
                        destinationDeviceId = tab.deviceId,
                        destinationPath = tab.path,
                        conflicts = conflicts,
                    )
                }
            } catch (e: Exception) {
                toast("检查目标目录失败：${e.message}")
            }
        }
    }

    Column(modifier) {
        PaneTabBar(
            pane = pane,
            onNewTab = { pane.newTab(pane.current.deviceId, "/") },
            onCloseTab = { pane.closeTab(it) },
        )
        PaneToolbar(
            deviceId = tab.deviceId,
            displayName = displayNameOf(tab.deviceId, pairedDevices),
            showDeviceMenu = showDeviceMenu,
            onShowDeviceMenu = { showDeviceMenu = true },
            onDismissDeviceMenu = { showDeviceMenu = false },
            pairedDevices = pairedDevices.filter { it.type != "windows" },
            onlineDeviceIds = onlineDeviceIds,
            onSelectDevice = { deviceId ->
                showDeviceMenu = false
                if (deviceId != tab.deviceId) switchDevice(deviceId)
            },
            searchActive = searchActive,
            onToggleSearch = {
                searchActive = !searchActive
                if (!searchActive) {
                    tab.searchQuery = ""
                    tab.searchResults = null
                }
            },
            showSortMenu = showSortMenu,
            onShowSortMenu = { showSortMenu = true },
            onDismissSortMenu = { showSortMenu = false },
            sortMode = tab.sortMode,
            sortDescending = tab.sortDescending,
            onSelectSort = { mode ->
                tab.sortMode = mode
                showSortMenu = false
            },
            onToggleSortOrder = { tab.sortDescending = !tab.sortDescending },
            showFilterMenu = showFilterMenu,
            onShowFilterMenu = { showFilterMenu = true },
            onDismissFilterMenu = { showFilterMenu = false },
            filter = tab.filter,
            onSelectFilter = {
                tab.filter = it
                showFilterMenu = false
            },
            onShowSecondPane = onShowSecondPane,
            secondPaneVisible = secondPaneVisible,
            showMoreMenu = showMoreMenu,
            onShowMoreMenu = { showMoreMenu = true },
            onDismissMoreMenu = { showMoreMenu = false },
            onNewFolder = {
                showMoreMenu = false
                showNewFolder = true
            },
            onPaste = {
                showMoreMenu = false
                startPaste()
            },
            onFavoriteCurrent = {
                showMoreMenu = false
                bookmarkStore.addFavorite(
                    tab.deviceId,
                    tab.path,
                    FileProvider.nameOf(tab.path),
                    isDirectory = true
                )
                toast("已收藏当前目录")
            },
            onRefresh = {
                showMoreMenu = false
                refresh()
            },
            onSelectAll = {
                showMoreMenu = false
                tab.selectedPaths = visible.map { it.path }.toSet()
            },
        )

        if (searchActive) {
            OutlinedTextField(
                value = tab.searchQuery,
                onValueChange = { tab.searchQuery = it },
                modifier = Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 4.dp),
                placeholder = { Text("搜索当前目录及子目录") },
                singleLine = true,
                trailingIcon = {
                    if (tab.searchQuery.isNotEmpty()) {
                        IconButton(onClick = {
                            tab.searchQuery = ""
                            tab.searchResults = null
                        }) { Icon(Icons.Default.Close, contentDescription = "清除") }
                    }
                }
            )
        }

        BreadcrumbRow(
            path = tab.path,
            loading = tab.loading,
            onNavigate = { newPath ->
                tab.path = newPath
                tab.selectedPaths = emptySet()
                tab.searchQuery = ""
                tab.searchResults = null
            },
            onGoUp = {
                tab.path = FileProvider.parentOf(tab.path)
                tab.selectedPaths = emptySet()
                tab.searchQuery = ""
                tab.searchResults = null
            },
            onRefresh = { refresh() },
        )

        if (tab.searchResults != null) {
            Text(
                "搜索结果 ${visible.size} 项（基于 ${tab.path}）",
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(horizontal = 12.dp, vertical = 2.dp)
            )
        }

        Box(Modifier.weight(1f).fillMaxWidth()) {
            when {
                tab.error != null -> Text(
                    tab.error ?: "",
                    modifier = Modifier.padding(16.dp),
                    color = MaterialTheme.colorScheme.error,
                    style = MaterialTheme.typography.bodySmall
                )
                tab.loading && visible.isEmpty() -> LinearProgressIndicator(
                    Modifier.fillMaxWidth().align(Alignment.TopCenter)
                )
            }
            LazyColumn(Modifier.fillMaxSize()) {
                if (visible.isEmpty() && !tab.loading && tab.error == null) {
                    item {
                        Text(
                            if (tab.searchResults != null) "没有匹配的文件"
                            else "空目录",
                            modifier = Modifier.padding(24.dp),
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                }
                items(items = visible, key = { it.path }) { entry ->
                    FileRow(
                        entry = entry,
                        selected = entry.path in tab.selectedPaths,
                        selectionMode = selectionMode,
                        onClick = {
                            if (selectionMode) {
                                tab.selectedPaths = toggleSelection(tab.selectedPaths, entry.path)
                            } else if (entry.isDirectory) {
                                tab.navigateTo(tab.deviceId, entry.path)
                            } else if (entry.iconKind in setOf(FileIconKind.VIDEO, FileIconKind.AUDIO, FileIconKind.DOCUMENT, FileIconKind.APK)) {
                                try { openExternalFile(context, tab.deviceId, entry) }
                                catch (error: Exception) { android.widget.Toast.makeText(context, error.message ?: "未找到支持该文件的应用", android.widget.Toast.LENGTH_LONG).show() }
                            } else {
                                previewTarget = entry
                            }
                        },
                        onLongClick = {
                            tab.selectedPaths = toggleSelection(tab.selectedPaths, entry.path)
                        },
                    )
                }
            }
        }

        if (selectionMode) {
            SelectionActionBar(
                count = tab.selectedPaths.size,
                singleSelected = selectedEntries.size == 1,
                onCopy = {
                    FileClipboard.put(ClipMode.COPY, tab.deviceId, selectedEntries)
                    tab.selectedPaths = emptySet()
                    toast("已复制 ${selectedEntries.size} 项，可切换目录粘贴")
                },
                onCut = {
                    FileClipboard.put(ClipMode.MOVE, tab.deviceId, selectedEntries)
                    tab.selectedPaths = emptySet()
                    toast("已剪切 ${selectedEntries.size} 项，可切换目录粘贴")
                },
                onDelete = { deleteTargets = selectedEntries },
                onRename = { renameTarget = selectedEntries.firstOrNull() },
                onProperties = { propertyTarget = selectedEntries.firstOrNull() },
                onFavorite = {
                    selectedEntries.firstOrNull()?.let { entry ->
                        bookmarkStore.addFavorite(
                            tab.deviceId, entry.path, entry.name, entry.isDirectory
                        )
                        toast("已收藏")
                    }
                },
                onCancel = { tab.selectedPaths = emptySet() },
            )
        }
    }

    // ---------------- 对话框

    renameTarget?.let { entry ->
        NameInputDialog(
            title = "重命名",
            initial = entry.name,
            confirmText = "重命名",
            onDismiss = { renameTarget = null },
            onConfirm = { newName ->
                renameTarget = null
                scope.launch {
                    try {
                        val provider = ProviderHub.providerFor(tab.deviceId)
                        val target = FileProvider.join(FileProvider.parentOf(entry.path), newName)
                        val ok = withContext(Dispatchers.IO) { provider.rename(entry.path, target) }
                        if (ok) {
                            tab.selectedPaths = emptySet()
                            refresh()
                            toast("已重命名")
                        } else {
                            toast("重命名失败：目标已存在或不允许操作")
                        }
                    } catch (e: Exception) {
                        toast("重命名失败：${e.message}")
                    }
                }
            }
        )
    }

    if (showNewFolder) {
        NameInputDialog(
            title = "新建文件夹",
            initial = "新建文件夹",
            confirmText = "创建",
            onDismiss = { showNewFolder = false },
            onConfirm = { name ->
                showNewFolder = false
                scope.launch {
                    try {
                        val provider = ProviderHub.providerFor(tab.deviceId)
                        val target = FileProvider.join(tab.path, name)
                        val ok = withContext(Dispatchers.IO) { provider.mkdir(target) }
                        if (ok) {
                            refresh()
                            toast("已创建：$name")
                        } else {
                            toast("创建失败")
                        }
                    } catch (e: Exception) {
                        toast("创建失败：${e.message}")
                    }
                }
            }
        )
    }

    if (deleteTargets.isNotEmpty()) {
        val targets = deleteTargets
        AlertDialog(
        icon = { BrandDialogIcon() },
            onDismissRequest = { deleteTargets = emptyList() },
            title = { Text("删除确认") },
            text = {
                Text(
                    "将删除 ${targets.size} 项：${targets.joinToString("、") { it.name }.take(120)}\n" +
                        "文件夹会连同其中所有内容一并删除，此操作不可撤销。"
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    deleteTargets = emptyList()
                    scope.launch {
                        var failed = 0
                        try {
                            val provider = ProviderHub.providerFor(tab.deviceId)
                            withContext(Dispatchers.IO) {
                                targets.forEach { entry ->
                                    if (!provider.delete(entry.path)) failed++
                                }
                            }
                            tab.selectedPaths = emptySet()
                            refresh()
                            toast(if (failed == 0) "已删除" else "有 $failed 项删除失败")
                        } catch (e: Exception) {
                            toast("删除失败：${e.message}")
                        }
                    }
                }) { Text("删除") }
            },
            dismissButton = {
                TextButton(onClick = { deleteTargets = emptyList() }) { Text("取消") }
            }
        )
    }

    propertyTarget?.let { entry ->
        PropertiesDialog(
            entry = entry,
            deviceName = displayNameOf(tab.deviceId, pairedDevices),
            isFavorite = bookmarkStore.isFavorite(tab.deviceId, entry.path),
            onToggleFavorite = {
                if (bookmarkStore.isFavorite(tab.deviceId, entry.path)) {
                    bookmarkStore.removeFavorite(tab.deviceId, entry.path)
                    toast("已取消收藏")
                } else {
                    bookmarkStore.addFavorite(tab.deviceId, entry.path, entry.name, entry.isDirectory)
                    toast("已收藏")
                }
            },
            onDismiss = { propertyTarget = null }
        )
    }

    previewTarget?.let { entry ->
        PreviewDialog(
            context = context,
            deviceId = tab.deviceId,
            entry = entry,
            onDismiss = { previewTarget = null },
        )
    }

    pendingPaste?.let { pending ->
        AlertDialog(
        icon = { BrandDialogIcon() },
            onDismissRequest = { pendingPaste = null },
            title = { Text("目标已存在同名文件") },
            text = {
                Text(
                    "以下 ${pending.conflicts.size} 个文件在目标目录已存在：\n" +
                        pending.conflicts.take(6).joinToString("、") +
                        (if (pending.conflicts.size > 6) "…" else "") +
                        "\n\n请选择处理方式（不会默认覆盖）。"
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    pendingPaste = null
                    enqueuePaste(ConflictPolicy.OVERWRITE)
                }) { Text("覆盖") }
            },
            dismissButton = {
                Column(horizontalAlignment = Alignment.End) {
                    Row {
                        TextButton(onClick = {
                            pendingPaste = null
                            enqueuePaste(ConflictPolicy.SKIP)
                        }) { Text("跳过") }
                        TextButton(onClick = {
                            pendingPaste = null
                            enqueuePaste(ConflictPolicy.RENAME)
                        }) { Text("重命名") }
                    }
                    Row {
                        TextButton(onClick = {
                            pendingPaste = null
                            enqueuePaste(ConflictPolicy.KEEP_BOTH)
                        }) { Text("保留两份") }
                        TextButton(onClick = { pendingPaste = null }) { Text("取消") }
                    }
                }
            }
        )
    }
}

@Composable
private fun PaneTabBar(
    pane: FilePaneState,
    onNewTab: () -> Unit,
    onCloseTab: (Int) -> Unit,
) {
    Row(
        Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
        verticalAlignment = Alignment.CenterVertically
    ) {
        pane.tabs.forEachIndexed { index, tab ->
            val selected = index == pane.selectedTab
            Row(
                modifier = Modifier
                    .padding(horizontal = 2.dp, vertical = 4.dp)
                    .background(
                        if (selected) MaterialTheme.colorScheme.secondaryContainer else Color.Transparent,
                        RoundedCornerShape(8.dp)
                    )
                    .clickable { pane.selectedTab = index }
                    .padding(start = 10.dp, end = 4.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                    tabTitle(tab),
                    style = MaterialTheme.typography.labelMedium,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                if (pane.tabs.size > 1) {
                    IconButton(
                        onClick = { onCloseTab(index) },
                        modifier = Modifier.size(28.dp)
                    ) { Icon(Icons.Default.Close, contentDescription = "关闭标签", Modifier.size(14.dp)) }
                }
            }
        }
        IconButton(onClick = onNewTab, modifier = Modifier.size(36.dp)) {
            Icon(Icons.Default.Add, contentDescription = "新建标签", Modifier.size(18.dp))
        }
    }
}

@Composable
private fun PaneToolbar(
    deviceId: String?,
    displayName: String,
    showDeviceMenu: Boolean,
    onShowDeviceMenu: () -> Unit,
    onDismissDeviceMenu: () -> Unit,
    pairedDevices: List<PairedDevice>,
    onlineDeviceIds: Set<String>,
    onSelectDevice: (String?) -> Unit,
    searchActive: Boolean,
    onToggleSearch: () -> Unit,
    showSortMenu: Boolean,
    onShowSortMenu: () -> Unit,
    onDismissSortMenu: () -> Unit,
    sortMode: FileSort,
    sortDescending: Boolean,
    onSelectSort: (FileSort) -> Unit,
    onToggleSortOrder: () -> Unit,
    showFilterMenu: Boolean,
    onShowFilterMenu: () -> Unit,
    onDismissFilterMenu: () -> Unit,
    filter: FileFilter,
    onSelectFilter: (FileFilter) -> Unit,
    onShowSecondPane: (() -> Unit)?,
    secondPaneVisible: Boolean,
    showMoreMenu: Boolean,
    onShowMoreMenu: () -> Unit,
    onDismissMoreMenu: () -> Unit,
    onNewFolder: () -> Unit,
    onPaste: () -> Unit,
    onFavoriteCurrent: () -> Unit,
    onRefresh: () -> Unit,
    onSelectAll: () -> Unit,
) {
    Row(
        Modifier.fillMaxWidth().padding(horizontal = 4.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Box {
            TextButton(onClick = onShowDeviceMenu) {
                Text(displayName, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            DropdownMenu(expanded = showDeviceMenu, onDismissRequest = onDismissDeviceMenu) {
                DropdownMenuItem(
                    text = { Text("本地存储" + if (deviceId == null) " ✓" else "") },
                    onClick = { onSelectDevice(null) }
                )
                pairedDevices.forEach { device ->
                    DropdownMenuItem(
                        text = {
                            Text(
                                device.name +
                                    if (device.deviceId in onlineDeviceIds) " · 在线" else " · 离线"
                            )
                        },
                        onClick = { onSelectDevice(device.deviceId) }
                    )
                }
                if (pairedDevices.isEmpty()) {
                    DropdownMenuItem(
                        text = { Text("暂无已配对设备，请到设备页配对") },
                        enabled = false,
                        onClick = {}
                    )
                }
            }
        }

        Spacer(Modifier.weight(1f))

        IconButton(onClick = onToggleSearch) {
            Icon(
                Icons.Default.Search,
                contentDescription = "搜索",
                tint = if (searchActive) MaterialTheme.colorScheme.primary
                else MaterialTheme.colorScheme.onSurfaceVariant
            )
        }

        Box {
            IconButton(onClick = onShowSortMenu) {
                Icon(Icons.Default.SwapVert, contentDescription = "排序")
            }
            DropdownMenu(expanded = showSortMenu, onDismissRequest = onDismissSortMenu) {
                FileSort.entries.forEach { mode ->
                    DropdownMenuItem(
                        text = { Text(mode.label + if (mode == sortMode) " ✓" else "") },
                        onClick = { onSelectSort(mode) }
                    )
                }
                DropdownMenuItem(
                    text = { Text(if (sortDescending) "当前：降序（点击切换升序）" else "当前：升序（点击切换降序）") },
                    onClick = onToggleSortOrder
                )
            }
        }

        Box {
            IconButton(onClick = onShowFilterMenu) {
                Icon(
                    Icons.Default.FilterList,
                    contentDescription = "筛选",
                    tint = if (filter != FileFilter.ALL) MaterialTheme.colorScheme.primary
                    else MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
            DropdownMenu(expanded = showFilterMenu, onDismissRequest = onDismissFilterMenu) {
                FileFilter.entries.forEach { item ->
                    DropdownMenuItem(
                        text = { Text(item.label + if (item == filter) " ✓" else "") },
                        onClick = { onSelectFilter(item) }
                    )
                }
            }
        }

        Box {
            IconButton(onClick = onShowMoreMenu) {
                Icon(Icons.Default.MoreVert, contentDescription = "更多")
            }
            DropdownMenu(expanded = showMoreMenu, onDismissRequest = onDismissMoreMenu) {
                DropdownMenuItem(
                    text = { Text("新建文件夹") },
                    leadingIcon = { Icon(Icons.Default.CreateNewFolder, null) },
                    onClick = onNewFolder
                )
                DropdownMenuItem(
                    text = { Text("粘贴" + if (FileClipboard.hasContent) "（${FileClipboard.entries.size} 项）" else "（剪贴板为空）") },
                    leadingIcon = { Icon(Icons.Default.ContentCopy, null) },
                    enabled = FileClipboard.hasContent,
                    onClick = onPaste
                )
                DropdownMenuItem(
                    text = { Text("收藏当前目录") },
                    leadingIcon = { Icon(Icons.Default.StarBorder, null) },
                    onClick = onFavoriteCurrent
                )
                DropdownMenuItem(
                    text = { Text("全选") },
                    leadingIcon = { Icon(Icons.Default.CheckCircle, null) },
                    onClick = onSelectAll
                )
                DropdownMenuItem(
                    text = { Text("刷新") },
                    leadingIcon = { Icon(Icons.Default.Refresh, null) },
                    onClick = onRefresh
                )
                if (onShowSecondPane != null) {
                    DropdownMenuItem(
                        text = { Text(if (secondPaneVisible) "关闭第二窗口" else "打开第二窗口") },
                        onClick = {
                            onDismissMoreMenu()
                            onShowSecondPane()
                        }
                    )
                }
            }
        }

        if (onShowSecondPane != null) {
            IconButton(onClick = onShowSecondPane) {
                Icon(
                    if (secondPaneVisible) Icons.Default.Close else Icons.Default.Add,
                    contentDescription = if (secondPaneVisible) "关闭第二窗口" else "打开第二窗口"
                )
            }
        }
    }
}

@Composable
private fun BreadcrumbRow(
    path: String,
    loading: Boolean,
    onNavigate: (String) -> Unit,
    onGoUp: () -> Unit,
    onRefresh: () -> Unit,
) {
    Row(
        Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
        verticalAlignment = Alignment.CenterVertically
    ) {
        IconButton(onClick = onGoUp, modifier = Modifier.size(36.dp)) {
            Icon(Icons.Default.ArrowUpward, contentDescription = "上一级", Modifier.size(18.dp))
        }
        val segments = pathSegments(path)
        segments.forEachIndexed { index, segment ->
            if (index > 0) {
                Text("/", color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            TextButton(onClick = { onNavigate(segment.second) }) {
                Text(
                    segment.first,
                    style = MaterialTheme.typography.labelMedium,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
            }
        }
        IconButton(onClick = onRefresh, modifier = Modifier.size(36.dp)) {
            Icon(Icons.Default.Refresh, contentDescription = "刷新", Modifier.size(18.dp))
        }
        if (loading) {
            LinearProgressIndicator(
                modifier = Modifier.width(40.dp).padding(horizontal = 4.dp)
            )
        }
    }
}

@Composable
private fun FileRow(
    entry: FileEntry,
    selected: Boolean,
    selectionMode: Boolean,
    onClick: () -> Unit,
    onLongClick: () -> Unit,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(
                if (selected) MaterialTheme.colorScheme.secondaryContainer else Color.Transparent
            )
            .pointerInput(entry.path, selectionMode) {
                detectTapGestures(
                    onTap = { onClick() },
                    onLongPress = { onLongClick() },
                )
            }
            .padding(horizontal = 12.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        if (selectionMode) {
            Checkbox(
                checked = selected,
                onCheckedChange = { onClick() },
                modifier = Modifier.size(32.dp)
            )
            Spacer(Modifier.width(4.dp))
        }
        Icon(
            imageVector = iconFor(entry.iconKind),
            contentDescription = null,
            tint = if (entry.isDirectory) MaterialTheme.colorScheme.primary
            else MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.size(28.dp)
        )
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(
                entry.name,
                style = MaterialTheme.typography.bodyMedium,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
            Text(
                if (entry.isDirectory) "文件夹 · ${formatTime(entry.modifiedAt)}"
                else "${formatSize(entry.size)} · ${formatTime(entry.modifiedAt)}",
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
        }
        if (entry.isSymlink) {
            Text("链接", style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.tertiary)
        }
    }
}

@Composable
private fun SelectionActionBar(
    count: Int,
    singleSelected: Boolean,
    onCopy: () -> Unit,
    onCut: () -> Unit,
    onDelete: () -> Unit,
    onRename: () -> Unit,
    onProperties: () -> Unit,
    onFavorite: () -> Unit,
    onCancel: () -> Unit,
) {
    Column {
        HorizontalDivider()
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 4.dp, vertical = 2.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text("已选 $count", style = MaterialTheme.typography.labelMedium,
                modifier = Modifier.padding(horizontal = 8.dp))
            Spacer(Modifier.weight(1f))
            IconButton(onClick = onCopy, modifier = Modifier.size(36.dp)) {
                Icon(Icons.Default.ContentCopy, contentDescription = "复制", Modifier.size(18.dp))
            }
            IconButton(onClick = onCut, modifier = Modifier.size(36.dp)) {
                Icon(Icons.Default.ContentCut, contentDescription = "剪切", Modifier.size(18.dp))
            }
            if (singleSelected) {
                IconButton(onClick = onRename, modifier = Modifier.size(36.dp)) {
                    Icon(Icons.Default.Edit, contentDescription = "重命名", Modifier.size(18.dp))
                }
                IconButton(onClick = onProperties, modifier = Modifier.size(36.dp)) {
                    Icon(Icons.Default.Info, contentDescription = "属性", Modifier.size(18.dp))
                }
                IconButton(onClick = onFavorite, modifier = Modifier.size(36.dp)) {
                    Icon(Icons.Default.StarBorder, contentDescription = "收藏", Modifier.size(18.dp))
                }
            }
            IconButton(onClick = onDelete, modifier = Modifier.size(36.dp)) {
                Icon(Icons.Default.Delete, contentDescription = "删除", Modifier.size(18.dp))
            }
            TextButton(onClick = onCancel) { Text("取消") }
        }
    }
}

@Composable
private fun NameInputDialog(
    title: String,
    initial: String,
    confirmText: String,
    onDismiss: () -> Unit,
    onConfirm: (String) -> Unit,
) {
    var value by remember { mutableStateOf(initial) }
    AlertDialog(
        icon = { BrandDialogIcon() },
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = {
            OutlinedTextField(
                value = value,
                onValueChange = { value = it },
                singleLine = true,
                label = { Text("名称") }
            )
        },
        confirmButton = {
            TextButton(
                onClick = { onConfirm(value.trim()) },
                enabled = value.trim().isNotEmpty()
            ) { Text(confirmText) }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("取消") } }
    )
}

@Composable
private fun PropertiesDialog(
    entry: FileEntry,
    deviceName: String,
    isFavorite: Boolean,
    onToggleFavorite: () -> Unit,
    onDismiss: () -> Unit,
) {
    var favorite by remember { mutableStateOf(isFavorite) }
    AlertDialog(
        icon = { BrandDialogIcon() },
        onDismissRequest = onDismiss,
        title = { Text("文件属性") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                PropertyLine("名称", entry.name)
                PropertyLine("路径", entry.path)
                PropertyLine("位置", deviceName)
                PropertyLine("类型", typeLabel(entry))
                PropertyLine("大小", if (entry.isDirectory) "—" else formatSize(entry.size))
                PropertyLine("修改时间", formatTime(entry.modifiedAt))
                PropertyLine("符号链接", if (entry.isSymlink) "是" else "否")
            }
        },
        confirmButton = {
            TextButton(onClick = {
                favorite = !favorite
                onToggleFavorite()
            }) {
                Icon(
                    if (favorite) Icons.Default.Star else Icons.Default.StarBorder,
                    contentDescription = null,
                    modifier = Modifier.size(16.dp)
                )
                Spacer(Modifier.width(4.dp))
                Text(if (favorite) "取消收藏" else "收藏")
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("关闭") } }
    )
}

@Composable
private fun PropertyLine(label: String, value: String) {
    Column {
        Text(label, style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(value, style = MaterialTheme.typography.bodySmall)
    }
}

@Composable
private fun PreviewDialog(
    context: Context,
    deviceId: String?,
    entry: FileEntry,
    onDismiss: () -> Unit,
) {
    when (entry.iconKind) {
        FileIconKind.IMAGE -> ImagePreviewDialog(context, deviceId, entry, onDismiss)
        FileIconKind.TEXT -> TextPreviewDialog(deviceId, entry, onDismiss)
        else -> AlertDialog(
        icon = { BrandDialogIcon() },
            onDismissRequest = onDismiss,
            title = { Text(entry.name) },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    Text("该文件类型暂不支持应用内预览。")
                    PropertyLine("大小", formatSize(entry.size))
                    PropertyLine("修改时间", formatTime(entry.modifiedAt))
                    PropertyLine("类型", typeLabel(entry))
                }
            },
            confirmButton = { TextButton(onClick = onDismiss) { Text("关闭") } }
        )
    }
}

@Composable
private fun TextPreviewDialog(
    deviceId: String?,
    entry: FileEntry,
    onDismiss: () -> Unit,
) {
    val context = LocalContext.current
    var content by remember { mutableStateOf<String?>(null) }
    var error by remember { mutableStateOf<String?>(null) }

    LaunchedEffect(entry.path) {
        try {
            val result = withContext(Dispatchers.IO) {
                val provider = ProviderHub.providerForBlocking(deviceId)
                provider.openRead(entry.path).use { input ->
                    val bytes = readLimited(input, 512 * 1024)
                    if (looksBinary(bytes)) null else bytes.toString(Charsets.UTF_8)
                }
            }
            if (result == null) {
                error = "二进制文件，无法以文本预览"
            } else {
                content = result
            }
        } catch (e: Exception) {
            error = e.message ?: "读取失败"
        }
    }

    AlertDialog(
        icon = { BrandDialogIcon() },
        onDismissRequest = onDismiss,
        title = { Text(entry.name) },
        text = {
            Box(
                Modifier
                    .fillMaxWidth()
                    .heightIn(max = 420.dp)
                    .verticalScroll(rememberScrollState())
            ) {
                Text(
                    content ?: error ?: "加载中…",
                    fontFamily = FontFamily.Monospace,
                    style = MaterialTheme.typography.bodySmall
                )
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text("关闭") } },
        dismissButton = { TextButton(onClick = {
            try { openExternalFile(context, deviceId, entry, edit = true) }
            catch (error: Exception) { android.widget.Toast.makeText(context, error.message, android.widget.Toast.LENGTH_LONG).show() }
        }) { Text("用其他应用编辑") } }
    )
}

@Composable
private fun ImagePreviewDialog(
    context: Context,
    deviceId: String?,
    entry: FileEntry,
    onDismiss: () -> Unit,
) {
    var bitmap by remember { mutableStateOf<Bitmap?>(null) }
    var error by remember { mutableStateOf<String?>(null) }

    LaunchedEffect(entry.path) {
        try {
            val decoded = withContext(Dispatchers.IO) {
                decodePreviewBitmap(context, deviceId, entry)
            }
            if (decoded == null) {
                error = "无法解码图片"
            } else {
                bitmap = decoded
            }
        } catch (e: Exception) {
            error = e.message ?: "图片加载失败"
        }
    }

    AlertDialog(
        icon = { BrandDialogIcon() },
        onDismissRequest = onDismiss,
        title = { Text(entry.name) },
        text = {
            Box(
                Modifier.fillMaxWidth().heightIn(max = 480.dp),
                contentAlignment = Alignment.Center
            ) {
                val image = bitmap
                if (image != null) {
                    ComposeImage(
                        bitmap = image.asImageBitmap(),
                        contentDescription = entry.name,
                        contentScale = ContentScale.Fit,
                        modifier = Modifier.fillMaxWidth()
                    )
                } else {
                    Text(error ?: "加载中…", style = MaterialTheme.typography.bodySmall)
                }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text("关闭") } }
    )
}

// ------------------------------------------------------------------ 工具函数

private fun decodePreviewBitmap(context: Context, deviceId: String?, entry: FileEntry): Bitmap? {
    if (entry.size > 64L * 1024 * 1024) {
        throw IllegalStateException("图片过大（${formatSize(entry.size)}），请用系统相册打开")
    }
    val provider = ProviderHub.providerForBlocking(deviceId)
    val localFile = provider.localFile(entry.path)
    val file: File
    var temporary = false
    if (localFile != null) {
        file = localFile
    } else {
        file = File(context.cacheDir, "preview_${System.currentTimeMillis()}.img")
        provider.openRead(entry.path).use { input ->
            file.outputStream().use { output -> input.copyTo(output, 64 * 1024) }
        }
        temporary = true
    }
    try {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(file.absolutePath, bounds)
        if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null
        var sample = 1
        while (bounds.outWidth / sample > 2048 || bounds.outHeight / sample > 2048) {
            sample *= 2
        }
        val options = BitmapFactory.Options().apply { inSampleSize = sample }
        return BitmapFactory.decodeFile(file.absolutePath, options)
    } finally {
        if (temporary) file.delete()
    }
}

private fun matchesFilter(entry: FileEntry, filter: FileFilter): Boolean = when (filter) {
    FileFilter.ALL -> true
    FileFilter.FOLDER -> entry.isDirectory
    FileFilter.FILE -> !entry.isDirectory
    FileFilter.IMAGE -> !entry.isDirectory && entry.iconKind == FileIconKind.IMAGE
    FileFilter.VIDEO -> !entry.isDirectory && entry.iconKind == FileIconKind.VIDEO
    FileFilter.AUDIO -> !entry.isDirectory && entry.iconKind == FileIconKind.AUDIO
    FileFilter.DOCUMENT -> !entry.isDirectory && entry.iconKind == FileIconKind.DOCUMENT
    FileFilter.ARCHIVE -> !entry.isDirectory && entry.iconKind == FileIconKind.ARCHIVE
}

private fun entryComparator(sort: FileSort, descending: Boolean): Comparator<FileEntry> =
    Comparator { a, b ->
        if (a.isDirectory != b.isDirectory) {
            return@Comparator if (a.isDirectory) -1 else 1
        }
        val primary = when (sort) {
            FileSort.NAME -> a.name.lowercase().compareTo(b.name.lowercase())
            FileSort.SIZE -> a.size.compareTo(b.size)
            FileSort.TIME -> a.modifiedAt.compareTo(b.modifiedAt)
            FileSort.TYPE -> a.iconKind.ordinal.compareTo(b.iconKind.ordinal)
        }
        val fallback = a.name.lowercase().compareTo(b.name.lowercase())
        val result = if (primary != 0) primary else fallback
        if (descending) -result else result
    }

private fun toggleSelection(current: Set<String>, path: String): Set<String> =
    if (path in current) current - path else current + path

private fun pathSegments(path: String): List<Pair<String, String>> {
    if (path == "/" || path.isBlank()) return listOf("根目录" to "/")
    val result = mutableListOf<Pair<String, String>>()
    var current = ""
    path.trim('/').split('/').filter { it.isNotEmpty() }.forEach { segment ->
        current = if (current.isEmpty()) "/$segment" else "$current/$segment"
        result.add(segment to current)
    }
    return if (result.isEmpty()) listOf("根目录" to "/") else result
}

private fun tabTitle(tab: FileTabState): String {
    val name = FileProvider.nameOf(tab.path)
    return if (name == "/") "根目录" else name
}

private fun displayNameOf(deviceId: String?, pairedDevices: List<PairedDevice>): String =
    if (deviceId == null) "本地存储"
    else pairedDevices.firstOrNull { it.deviceId == deviceId }?.name ?: "未配对设备"

private fun iconFor(kind: FileIconKind): ImageVector = when (kind) {
    FileIconKind.FOLDER -> Icons.Default.Folder
    FileIconKind.IMAGE -> Icons.Default.Image
    else -> Icons.Default.InsertDriveFile
}

private fun typeLabel(entry: FileEntry): String = when {
    entry.isDirectory -> "文件夹"
    entry.iconKind == FileIconKind.IMAGE -> "图片"
    entry.iconKind == FileIconKind.VIDEO -> "视频"
    entry.iconKind == FileIconKind.AUDIO -> "音频"
    entry.iconKind == FileIconKind.DOCUMENT -> "文档"
    entry.iconKind == FileIconKind.ARCHIVE -> "压缩包"
    entry.iconKind == FileIconKind.APK -> "安装包"
    entry.iconKind == FileIconKind.TEXT -> "文本"
    else -> "文件"
}
