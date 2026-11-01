package com.phonedrive.android.ui

import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.size
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.unit.dp
import com.phonedrive.android.R

@Composable
fun BrandDialogIcon() {
    Image(painterResource(R.drawable.ic_launcher_brand), contentDescription = "PhoneDrive", modifier = Modifier.size(40.dp))
}
