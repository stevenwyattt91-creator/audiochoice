package com.audiochoice.mobile.playback

import android.app.PendingIntent
import android.content.Intent
import androidx.annotation.OptIn
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.session.DefaultMediaNotificationProvider
import androidx.media3.session.MediaSession
import androidx.media3.session.MediaSessionService
import com.audiochoice.mobile.MainActivity
import com.audiochoice.mobile.R

/**
 * Owns the ExoPlayer instance for the whole process.
 *
 * Playback previously lived in an Activity-scoped ViewModel, which meant the
 * process became a cached process the moment the Activity stopped: Android could
 * kill an audiobook mid-chapter, and release builds had no lock-screen or
 * notification controls at all. Hosting the player in a MediaSessionService with
 * foregroundServiceType="mediaPlayback" keeps playback alive in the background
 * and gives every build variant the system media surface for free.
 *
 * Filter enforcement deliberately stays in PlayerViewModel. This service owns
 * the transport only, so there is still exactly one place that decides which
 * content ranges to skip.
 */
@OptIn(UnstableApi::class)
class AudioChoicePlaybackService : MediaSessionService() {

    private var mediaSession: MediaSession? = null

    override fun onCreate() {
        super.onCreate()

        val player = ExoPlayer.Builder(this)
            .setSeekBackIncrementMs(SEEK_INCREMENT_MS)
            .setSeekForwardIncrementMs(SEEK_INCREMENT_MS)
            // Audiobooks are speech, so request speech attributes and let Media3
            // manage audio focus. Without this the book talked over phone calls
            // and navigation prompts and never ducked.
            .setAudioAttributes(
                AudioAttributes.Builder()
                    .setUsage(C.USAGE_MEDIA)
                    .setContentType(C.AUDIO_CONTENT_TYPE_SPEECH)
                    .build(),
                /* handleAudioFocus = */ true,
            )
            // Unplugging headphones must pause rather than switch to the speaker.
            .setHandleAudioBecomingNoisy(true)
            // Holds a partial wake lock only while playing, so the CPU cannot
            // sleep mid-chapter and stall playback.
            .setWakeMode(C.WAKE_MODE_LOCAL)
            .build()

        val openAppIntent = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java).apply {
                flags = Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
            },
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )

        mediaSession = MediaSession.Builder(this, player)
            .setSessionActivity(openAppIntent)
            .build()

        setMediaNotificationProvider(
            DefaultMediaNotificationProvider.Builder(this)
                .setChannelId(NOTIFICATION_CHANNEL_ID)
                .setChannelName(R.string.playback_channel_name)
                .build()
                .apply { setSmallIcon(R.mipmap.ic_launcher) },
        )
    }

    override fun onGetSession(controllerInfo: MediaSession.ControllerInfo): MediaSession? = mediaSession

    /**
     * Swiping the app away should not leave an orphaned notification behind when
     * nothing is playing. An actively playing book is left alone so the listener
     * keeps their audio.
     */
    override fun onTaskRemoved(rootIntent: Intent?) {
        val player = mediaSession?.player
        if (player == null || !player.playWhenReady || player.mediaItemCount == 0) {
            stopSelf()
        }
        super.onTaskRemoved(rootIntent)
    }

    override fun onDestroy() {
        mediaSession?.run {
            player.release()
            release()
        }
        mediaSession = null
        super.onDestroy()
    }

    private companion object {
        const val SEEK_INCREMENT_MS = 30_000L
        const val NOTIFICATION_CHANNEL_ID = "audiochoice_playback"
    }
}
