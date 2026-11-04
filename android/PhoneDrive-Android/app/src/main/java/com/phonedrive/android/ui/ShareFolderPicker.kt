package com.phonedrive.android.ui

import android.os.Environment
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.phonedrive.android.server.ShareDirectories
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

@Composable
fun ShareFolderPicker(initial: String, onDismiss: () -> Unit, onSelect: (String) -> Unit) {
    val base = remember { Environment.getExternalStorageDirectory().canonicalFile }
    var current by remember { mutableStateOf(runCatching { ShareDirectories.validate(base, File(base, initial)) }.getOrDefault(base)) }
    var folders by remember { mutableStateOf(emptyList<File>()) }
    var error by remember { mutableStateOf<String?>(null) }
    var loading by remember { mutableStateOf(true) }
    var refresh by remember { mutableIntStateOf(0) }
    var create by remember { mutableStateOf(false) }
    var name by remember { mutableStateOf("") }
    var creating by remember { mutableStateOf(false) }
    var createError by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()
    LaunchedEffect(current, refresh) {
        loading = true; error = null; folders = emptyList()
        val result = withContext(Dispatchers.IO) { runCatching {
            ShareDirectories.validate(base, current)
            (current.listFiles() ?: error("无法读取此目录，请检查所有文件访问权限"))
                .filter { it.isDirectory && runCatching { ShareDirectories.validate(base, it) }.isSuccess }
                .sortedBy { it.name.lowercase() }
        } }
        result.onSuccess { folders = it }.onFailure { error = it.message }
        loading = false
    }
    AlertDialog(onDismissRequest = onDismiss, title = { Text("选择共享文件夹") }, text = {
        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("内部共享存储 / " + current.relativeTo(base).path.replace('\\','/'))
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                TextButton(enabled = current != base && !loading, onClick = { current = current.parentFile ?: base }) { Text("上一级") }
                TextButton(enabled = !loading && error == null, onClick = { name = ""; createError = null; create = true }) { Text("新建文件夹") }
            }
            if (loading) LinearProgressIndicator(Modifier.fillMaxWidth())
            error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            LazyColumn(Modifier.fillMaxWidth().heightIn(min = 160.dp, max = 320.dp)) {
                items(folders, key = { it.path }) { folder ->
                    Text("▸  " + folder.name, Modifier.fillMaxWidth().clickable { current = folder }.padding(vertical = 14.dp))
                }
                if (!loading && folders.isEmpty() && error == null) item { Text("此目录没有子文件夹，可以直接选择或新建。") }
            }
        }
    }, confirmButton = { TextButton(enabled = !loading && error == null, onClick = {
        runCatching { ShareDirectories.validate(base, current) }.onSuccess { onSelect(it.relativeTo(base).path.replace('\\','/')) }.onFailure { error = it.message }
    }) { Text("选择当前文件夹") } }, dismissButton = { TextButton(onClick = onDismiss) { Text("取消") } })
    if (create) AlertDialog(onDismissRequest = { if (!creating) create = false }, title = { Text("在当前目录新建文件夹") }, text = {
        Column {
            OutlinedTextField(value = name, onValueChange = { name = it; createError = null }, label = { Text("文件夹名称") }, singleLine = true, enabled = !creating)
            createError?.let { Text(it, color = MaterialTheme.colorScheme.error) }
        }
    }, confirmButton = { TextButton(enabled = !creating && name.isNotBlank(), onClick = {
        creating = true
        scope.launch {
            val result = withContext(Dispatchers.IO) { runCatching { ShareDirectories.create(base, current, name) } }
            result.onSuccess { create = false; refresh++ }.onFailure { createError = it.message }
            creating = false
        }
    }) { Text(if (creating) "创建中…" else "创建") } }, dismissButton = { TextButton(enabled = !creating, onClick = { create = false }) { Text("取消") } })
}
