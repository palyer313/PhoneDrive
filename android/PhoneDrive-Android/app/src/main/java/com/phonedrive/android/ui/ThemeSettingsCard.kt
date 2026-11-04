package com.phonedrive.android.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.phonedrive.android.ui.theme.ThemeSettings

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun ThemeSettingsCard() {
    val context = LocalContext.current
    val prefs = remember { ThemeSettings.prefs(context) }
    var mode by remember { mutableStateOf(prefs.getString("mode", "system") ?: "system") }
    var accent by remember { mutableStateOf(prefs.getString("accent", "#1565C0") ?: "#1565C0") }
    var picker by remember { mutableStateOf(false) }
    fun save(hex: String) { accent = hex; prefs.edit().putString("accent", hex).apply() }
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("外观与主题", style = MaterialTheme.typography.titleMedium)
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                listOf("system" to "跟随系统", "light" to "浅色", "dark" to "夜间模式").forEach { (key, label) ->
                    FilterChip(selected = mode == key, onClick = { mode = key; prefs.edit().putString("mode", key).apply() }, label = { Text(label) })
                }
            }
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                ThemeSettings.presets.forEach { (label, hex) ->
                    FilterChip(selected = accent.equals(hex, true), onClick = { save(hex) },
                        leadingIcon = { Box(Modifier.size(16.dp).background(ThemeSettings.color(hex), CircleShape)) }, label = { Text(label) })
                }
            }
            OutlinedButton(onClick = { picker = true }) { Text("颜色盘 · $accent") }
            Text("主题色用于按钮、选中项与强调色，设置自动保存。", style = MaterialTheme.typography.bodySmall)
        }
    }
    if (picker) ColorPickerDialog(accent, { picker = false }) { save(it); picker = false }
}

@Composable
private fun ColorPickerDialog(initial: String, onDismiss: () -> Unit, onSave: (String) -> Unit) {
    val start = remember { FloatArray(3).also { android.graphics.Color.colorToHSV(ThemeSettings.color(initial).toArgb(), it) } }
    var hue by remember { mutableFloatStateOf(start[0]) }
    var saturation by remember { mutableFloatStateOf(start[1]) }
    var brightness by remember { mutableFloatStateOf(start[2]) }
    var hex by remember { mutableStateOf(initial) }
    fun updateHex() { hex = "#%06X".format(android.graphics.Color.HSVToColor(floatArrayOf(hue, saturation, brightness)) and 0xFFFFFF) }
    val valid = hex.matches(Regex("#[0-9a-fA-F]{6}"))
    AlertDialog(onDismissRequest = onDismiss, title = { Text("自定义主题色") }, text = {
        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Canvas(Modifier.fillMaxWidth().height(180.dp)
                .pointerInput(hue) {
                    detectTapGestures { pos -> saturation = (pos.x / size.width).coerceIn(0f,1f); brightness = (1f-pos.y/size.height).coerceIn(0f,1f); updateHex() }
                }.pointerInput(hue) {
                    detectDragGestures { change, _ -> change.consume(); saturation = (change.position.x/size.width).coerceIn(0f,1f); brightness = (1f-change.position.y/size.height).coerceIn(0f,1f); updateHex() }
                }) {
                drawRect(Brush.horizontalGradient(listOf(Color.White, Color.hsv(hue,1f,1f))))
                drawRect(Brush.verticalGradient(listOf(Color.Transparent, Color.Black)))
                val point = Offset(saturation*size.width, (1f-brightness)*size.height)
                drawCircle(Color.Black, 8.dp.toPx(), point)
                drawCircle(Color.White, 5.dp.toPx(), point)
            }
            Text("色相")
            Slider(value = hue, onValueChange = { hue = it; updateHex() }, valueRange = 0f..359.9f)
            Box(Modifier.fillMaxWidth().height(32.dp).background(ThemeSettings.color(hex)))
            OutlinedTextField(value = hex, onValueChange = {
                hex = it
                if (it.matches(Regex("#[0-9a-fA-F]{6}"))) {
                    val hsv = FloatArray(3); android.graphics.Color.colorToHSV(android.graphics.Color.parseColor(it), hsv)
                    hue = hsv[0]; saturation = hsv[1]; brightness = hsv[2]
                }
            }, label = { Text("颜色值 #RRGGBB") }, isError = !valid, singleLine = true)
        }
    }, confirmButton = { TextButton(enabled = valid, onClick = { onSave(hex.uppercase()) }) { Text("应用") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("取消") } })
}
