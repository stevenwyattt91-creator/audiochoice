package com.audiochoice.mobile.beta

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.os.Build
import androidx.annotation.OptIn
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.session.MediaSession
import androidx.media3.ui.PlayerNotificationManager
import com.audiochoice.mobile.MainActivity
import com.audiochoice.mobile.R
import java.io.File
import java.util.UUID

/**
 * Connects the beta player's existing Media3 instance to Android's system
 * media surface (notification, lock screen, and device compact media player).
 * It deliberately owns no playback state, so filter enforcement continues to
 * live in PlayerViewModel.
 */
@OptIn(UnstableApi::class)
class BetaPlaybackControls(
    context: Context,
    player: Player,
) {
    private val applicationContext = context.applicationContext
    private var title: String = "AudioChoice"
    private var author: String? = null
    private var coverPath: String? = null

    private val contentIntent = PendingIntent.getActivity(
        applicationContext,
        0,
        Intent(applicationContext, MainActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
        },
        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
    )

    private val mediaSession = MediaSession.Builder(applicationContext, player)
        // A transfer QR can bring an already-running Beta activity to the
        // foreground. Give every live controller its own Media3 session ID so
        // an Android activity recreation can never prevent the import screen
        // from opening.
        .setId("audiochoice-beta-${UUID.randomUUID()}")
        .setSessionActivity(contentIntent)
        .build()

    private val notificationManager: PlayerNotificationManager

    init {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            applicationContext.getSystemService(NotificationManager::class.java)
                .createNotificationChannel(
                    NotificationChannel(
                        CHANNEL_ID,
                        "Audiobook playback",
                        NotificationManager.IMPORTANCE_LOW,
                    ).apply {
                        description = "Playback controls for the current audiobook"
                        setShowBadge(false)
                    },
                )
        }

        notificationManager = PlayerNotificationManager.Builder(
            applicationContext,
            NOTIFICATION_ID,
            CHANNEL_ID,
        ).setMediaDescriptionAdapter(object : PlayerNotificationManager.MediaDescriptionAdapter {
            override fun getCurrentContentTitle(player: Player): CharSequence = title

            override fun createCurrentContentIntent(player: Player): PendingIntent = contentIntent

            override fun getCurrentContentText(player: Player): CharSequence? = author

            override fun getCurrentLargeIcon(
                player: Player,
                callback: PlayerNotificationManager.BitmapCallback,
            ): Bitmap? = coverPath
                ?.let(::File)
                ?.takeIf(File::isFile)
                ?.let { BitmapFactory.decodeFile(it.absolutePath) }
        }).build().apply {
            setMediaSessionToken(mediaSession.platformToken)
            setUseRewindAction(true)
            setUseFastForwardAction(true)
            setUseRewindActionInCompactView(true)
            setUseFastForwardActionInCompactView(true)
            setUsePreviousAction(false)
            setUseNextAction(false)
            setSmallIcon(R.mipmap.ic_launcher)
            setPlayer(player)
        }
    }

    fun updateMetadata(title: String, author: String?, coverPath: String?) {
        this.title = title
        this.author = author
        this.coverPath = coverPath
        notificationManager.invalidate()
    }

    fun release() {
        notificationManager.setPlayer(null)
        mediaSession.release()
    }

    private companion object {
        const val CHANNEL_ID = "audiochoice_beta_playback"
        const val NOTIFICATION_ID = 4102
    }
}
