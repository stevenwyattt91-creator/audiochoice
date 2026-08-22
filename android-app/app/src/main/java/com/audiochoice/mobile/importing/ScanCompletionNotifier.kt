package com.audiochoice.mobile.importing

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import com.audiochoice.mobile.MainActivity
import com.audiochoice.mobile.R

class ScanCompletionNotifier(private val context: Context) {
    private val manager = context.getSystemService(NotificationManager::class.java)

    fun notifyReady(fileName: String) {
        if (Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) return

        val channelID = "scan_completion"
        manager.createNotificationChannel(
            NotificationChannel(channelID, "Audiobook scan updates", NotificationManager.IMPORTANCE_DEFAULT),
        )
        val openApp = PendingIntent.getActivity(
            context,
            0,
            Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        manager.notify(
            fileName.hashCode(),
            NotificationCompat.Builder(context, channelID)
                .setSmallIcon(R.mipmap.ic_launcher)
                .setContentTitle("Your AudioChoice scan is ready")
                .setContentText("$fileName is ready for filtered listening.")
                .setContentIntent(openApp)
                .setAutoCancel(true)
                .build(),
        )
    }
}
