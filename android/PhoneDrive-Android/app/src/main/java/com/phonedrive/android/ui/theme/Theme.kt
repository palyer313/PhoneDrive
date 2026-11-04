package com.phonedrive.android.ui.theme

import android.content.Context
import android.content.SharedPreferences
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.lerp
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalContext

object ThemeSettings {
    fun prefs(context: Context) = context.getSharedPreferences("phonedrive_theme", Context.MODE_PRIVATE)
    val presets = listOf("海蓝" to "#1565C0", "翡翠" to "#008577", "紫罗兰" to "#7C4DFF", "玫瑰" to "#C2185B", "琥珀" to "#C77700")
    fun color(hex: String): Color = runCatching { Color(android.graphics.Color.parseColor(hex)) }.getOrDefault(Color(0xFF1565C0))
}

@Composable
fun PhoneDriveTheme(darkTheme: Boolean = isSystemInDarkTheme(), content: @Composable () -> Unit) {
    val context = LocalContext.current
    val prefs = remember { ThemeSettings.prefs(context) }
    var revision by remember { mutableIntStateOf(0) }
    DisposableEffect(prefs) {
        val listener = SharedPreferences.OnSharedPreferenceChangeListener { _, _ -> revision++ }
        prefs.registerOnSharedPreferenceChangeListener(listener)
        onDispose { prefs.unregisterOnSharedPreferenceChangeListener(listener) }
    }
    val mode = remember(revision) { prefs.getString("mode", "system") }
    val accent = remember(revision) { ThemeSettings.color(prefs.getString("accent", "#1565C0") ?: "#1565C0") }
    val dark = when(mode) { "dark" -> true; "light" -> false; else -> darkTheme }
    val base = if (dark) darkColorScheme() else lightColorScheme()
    SideEffect {
        (context as? android.app.Activity)?.window?.let { window ->
            window.statusBarColor = base.surface.toArgb()
            window.navigationBarColor = base.surface.toArgb()
            window.setBackgroundDrawable(android.graphics.drawable.ColorDrawable(base.surface.toArgb()))
            androidx.core.view.WindowCompat.getInsetsController(window, window.decorView).apply {
                isAppearanceLightStatusBars = !dark
                isAppearanceLightNavigationBars = !dark
            }
        }
    }
    val onAccent = if (accent.luminance() > 0.179f) Color.Black else Color.White
    val container = lerp(base.surface, accent, if (dark) 0.30f else 0.14f)
    MaterialTheme(colorScheme = base.copy(primary = accent, onPrimary = onAccent,
        primaryContainer = container, onPrimaryContainer = base.onSurface,
        secondary = accent, onSecondary = onAccent, secondaryContainer = container,
        onSecondaryContainer = base.onSurface, tertiary = accent, onTertiary = onAccent), content = content)
}
