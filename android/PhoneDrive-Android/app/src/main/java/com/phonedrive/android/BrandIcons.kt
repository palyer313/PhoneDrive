package com.phonedrive.android

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import androidx.core.content.ContextCompat

object BrandIcons {
    fun bitmap(context: Context): Bitmap {
        val image = Bitmap.createBitmap(128, 128, Bitmap.Config.ARGB_8888)
        ContextCompat.getDrawable(context, R.drawable.ic_launcher_brand)?.let {
            it.setBounds(0, 0, 128, 128)
            it.draw(Canvas(image))
        }
        return image
    }
}
