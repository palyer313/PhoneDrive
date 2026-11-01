package com.phonedrive.android

import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import com.phonedrive.android.ui.PhoneDriveRoot
import com.phonedrive.android.ui.theme.PhoneDriveTheme

class MainActivity : ComponentActivity() {

    private val notificationPermission =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        @Suppress("DEPRECATION")
        setTaskDescription(android.app.ActivityManager.TaskDescription("PhoneDrive", BrandIcons.bitmap(this), android.graphics.Color.rgb(23,105,210)))
        if (Build.VERSION.SDK_INT >= 33) {
            notificationPermission.launch(android.Manifest.permission.POST_NOTIFICATIONS)
        }
        setContent {
            PhoneDriveTheme {
                PhoneDriveRoot()
            }
        }
    }
}
