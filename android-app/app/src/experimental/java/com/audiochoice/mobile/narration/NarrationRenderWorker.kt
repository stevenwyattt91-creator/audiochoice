package com.audiochoice.mobile.narration

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.content.pm.ServiceInfo
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.work.CoroutineWorker
import androidx.work.ExistingWorkPolicy
import androidx.work.ForegroundInfo
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import androidx.work.workDataOf
import com.audiochoice.mobile.R

/**
 * The background half of rendering, and deliberately almost nothing else.
 *
 * Everything about which chapter to produce, what to persist and how to recover from
 * an interruption lives in [NarrationRenderCoordinator], which is why that has
 * twenty-odd tests and this has none: what remains here is `WorkManager` wiring and a
 * notification, neither of which a unit test can meaningfully assert.
 *
 * Unique work per book. `ExistingWorkPolicy.KEEP` is what enforces one chapter at a
 * time: a second request for the same book while one is running is dropped rather than
 * queued, so two workers can never encode into the same chapter file. The same
 * mechanism the existing scan-status worker uses for the active scan.
 *
 * Foreground, because a listener starts a book and then puts their phone in a pocket.
 * Rendering that stops the moment the app is backgrounded would mean the next chapter
 * is never ready when they reach it.
 */
class NarrationRenderWorker(
    private val context: Context,
    parameters: WorkerParameters,
) : CoroutineWorker(context, parameters) {

    override suspend fun doWork(): Result {
        if (!NarrationConfig.enabled) return Result.success()
        val sha256 = inputData.getString(KEY_SHA256) ?: return Result.success()
        val title = inputData.getString(KEY_TITLE).orEmpty()

        setForegroundSafely(notification(title, rendered = 0, total = 0))

        // The coordinator is assembled by the caller in the experimental build, which
        // owns the voice engine and the store. Until that wiring lands with the import
        // flow, a worker with no coordinator simply reports success rather than
        // pretending to render.
        val coordinator = NarrationRenderWork.coordinatorFactory?.invoke(context, sha256)
            ?: return Result.success()
        val work = NarrationRenderWork.workFactory?.invoke(context, sha256)
            ?: return Result.success()

        val pass = coordinator.renderPending(
            sha256 = sha256,
            plan = work.plan,
            filteredRanges = work.filteredRanges,
            playheadChapter = work.playheadChapter,
            fullBookRequested = work.fullBookRequested,
            pausedByListener = work.pausedByListener,
        )

        return when (pass.stopReason) {
            // More chapters remain but the window is satisfied. Completing rather than
            // holding the worker alive is what keeps a paused book from occupying a
            // foreground service for hours; the next trigger re-enqueues it.
            StopReason.WINDOW_SATISFIED,
            StopReason.NOTHING_LEFT,
            StopReason.PAUSED,
            StopReason.ALL_FAILED,
            // Out of storage completes rather than retries. A retry under WorkManager's backoff
            // would wake the device repeatedly to discover the same full disk, and the listener is
            // the only one who can fix it -- so the queue and the rendered chapters are kept, and
            // the next explicit attempt continues from where this stopped.
            StopReason.OUT_OF_STORAGE,
            -> Result.success()

            // The system took the worker away. Retry lets `WorkManager` bring it back
            // under its own backoff rather than losing the remaining chapters.
            StopReason.CANCELLED -> Result.retry()
        }
    }

    /**
     * `setForeground` can be refused, and on some versions throws, when the app is in a
     * state that forbids starting a foreground service. Rendering is still worth doing
     * in that case, just without the notification, so this must not fail the job.
     */
    private suspend fun setForegroundSafely(notification: Notification) {
        runCatching { setForeground(foregroundInfo(notification)) }
    }

    /**
     * The service type this work should claim.
     *
     * Never the media playback type. Nothing is playing here; this produces audio for
     * later, and claiming playback would be describing the work as something it is not
     * to get a longer-lived service.
     *
     * Android 15 added a media-processing type that describes exactly this, so it is
     * preferred where it exists and the general data-sync type is used below that. Both
     * are declared in the experimental manifest, and both carry a daily runtime budget
     * on recent platforms, which is one more reason rendering is bounded by the window
     * rather than running to the end of a book.
     */
    private fun foregroundInfo(notification: Notification): ForegroundInfo = when {
        Build.VERSION.SDK_INT >= 35 -> ForegroundInfo(
            NOTIFICATION_ID,
            notification,
            ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROCESSING,
        )

        Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE -> ForegroundInfo(
            NOTIFICATION_ID,
            notification,
            ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC,
        )

        else -> ForegroundInfo(NOTIFICATION_ID, notification)
    }

    private fun notification(bookTitle: String, rendered: Int, total: Int): Notification {
        ensureChannel()
        val progress = if (total > 0) "Chapter $rendered of $total" else "Preparing"
        return NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_launcher_foreground)
            .setContentTitle(bookTitle.ifBlank { "Preparing narration" })
            .setContentText(progress)
            .setOngoing(true)
            .setSilent(true)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .build()
    }

    private fun ensureChannel() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val manager = context.getSystemService(NotificationManager::class.java) ?: return
        if (manager.getNotificationChannel(CHANNEL_ID) != null) return
        manager.createNotificationChannel(
            NotificationChannel(
                CHANNEL_ID,
                "Narration",
                // Low: this is a progress indicator for something the listener asked
                // for, not news.
                NotificationManager.IMPORTANCE_LOW,
            ).apply { setShowBadge(false) },
        )
    }

    companion object {
        private const val KEY_SHA256 = "sha256"
        private const val KEY_TITLE = "title"
        private const val CHANNEL_ID = "audiochoice-narration"
        private const val NOTIFICATION_ID = 4_120

        /** One unique work name per book, so two books can render independently. */
        fun uniqueWorkName(sha256: String) = "audiochoice-narration-${sha256.lowercase()}"

        /**
         * Ask for a render pass.
         *
         * `KEEP` rather than `REPLACE`: a trigger arriving while a chapter is being
         * encoded must not cancel it and start again, which would make a book that is
         * triggered often never finish a chapter.
         */
        fun enqueue(context: Context, sha256: String, bookTitle: String) {
            if (!NarrationConfig.enabled) return
            val request = OneTimeWorkRequestBuilder<NarrationRenderWorker>()
                .setInputData(workDataOf(KEY_SHA256 to sha256, KEY_TITLE to bookTitle))
                .build()
            WorkManager.getInstance(context)
                .enqueueUniqueWork(uniqueWorkName(sha256), ExistingWorkPolicy.KEEP, request)
        }

        fun cancel(context: Context, sha256: String) {
            WorkManager.getInstance(context).cancelUniqueWork(uniqueWorkName(sha256))
        }
    }
}

/**
 * What the worker needs that only the app can assemble.
 *
 * A `WorkManager` worker is constructed by the framework, so it cannot be handed a
 * voice engine, a store or the current playhead. These hooks are set once at startup by
 * the experimental build. Left unset -- in beta and release, where narration does not
 * exist -- the worker does nothing rather than half of something.
 */
object NarrationRenderWork {

    data class Snapshot(
        val plan: com.audiochoice.mobile.data.NarrationPlan,
        val filteredRanges: List<com.audiochoice.mobile.reader.ReaderMask>,
        val playheadChapter: Int,
        val fullBookRequested: Boolean,
        val pausedByListener: Boolean,
    )

    var coordinatorFactory: ((Context, String) -> NarrationRenderCoordinator?)? = null
    var workFactory: ((Context, String) -> Snapshot?)? = null
}
