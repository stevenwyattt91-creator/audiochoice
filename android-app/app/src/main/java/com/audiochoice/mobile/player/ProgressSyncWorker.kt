package com.audiochoice.mobile.player

import android.content.Context
import androidx.work.BackoffPolicy
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import com.audiochoice.mobile.data.ApiException
import com.audiochoice.mobile.data.AudioChoiceApi
import com.audiochoice.mobile.data.SessionStore
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.flow.first
import kotlinx.serialization.json.Json

/**
 * Pushes listening positions that were saved locally but never reached the
 * server.
 *
 * PlayerViewModel.onCleared() used to launch its final network save into
 * viewModelScope, which Android has already cancelled by that point, so the last
 * position of a session silently never synced. Any mid-session save that failed
 * had the same problem: the dirty flag stayed set with nothing scheduled to
 * retry it.
 *
 * Running the drain in WorkManager means it survives both ViewModel teardown and
 * process death, and retries with backoff once connectivity returns.
 */
class ProgressSyncWorker(
    applicationContext: Context,
    parameters: WorkerParameters,
) : CoroutineWorker(applicationContext, parameters) {

    override suspend fun doWork(): Result {
        val json = Json { ignoreUnknownKeys = true; encodeDefaults = true }
        // Nothing to sync against without a session. hydrateAccountProgress will
        // reconcile these checkpoints on the next sign-in.
        val session = SessionStore(applicationContext, json).session.first() ?: return Result.success()
        val preferences = applicationContext.getSharedPreferences(
            PlaybackProgressKeys.PREFERENCES_NAME,
            Context.MODE_PRIVATE,
        )
        val api = AudioChoiceApi(json)

        val dirtyBookIDs = preferences.all.keys
            .filter { PlaybackProgressKeys.isDirtyKey(it) && preferences.getBoolean(it, false) }
            .map(PlaybackProgressKeys::bookIDFromDirtyKey)
        if (dirtyBookIDs.isEmpty()) return Result.success()

        var retryNeeded = false
        for (bookID in dirtyBookIDs) {
            val positionMs = preferences.getLong(PlaybackProgressKeys.positionKey(bookID), -1L)
            if (positionMs < 0L) continue
            // Completion travels with the position because the server assigns both in one
            // write. Without it this worker would silently un-finish every book it synced.
            val isFinished = preferences.getBoolean(
                PlaybackProgressKeys.finishedKey(bookID),
                false,
            )
            val outcome = runCatching {
                api.saveProgress(session.accessToken, bookID, positionMs / 1000.0, isFinished)
            }
            outcome.onSuccess {
                // Only clear the flag if the stored position is still the one we
                // just sent; a newer save may have landed while this ran.
                val current = preferences.getLong(PlaybackProgressKeys.positionKey(bookID), -1L)
                if (current == positionMs) {
                    preferences.edit().putBoolean(PlaybackProgressKeys.dirtyKey(bookID), false).apply()
                }
            }.onFailure { error ->
                // A rejected session will not recover by retrying the same token.
                if (error is ApiException && error.statusCode == 401) return Result.failure()
                retryNeeded = true
            }
        }
        return if (retryNeeded) Result.retry() else Result.success()
    }

    companion object {
        private const val UNIQUE_WORK = "audiochoice-progress-sync"

        /**
         * Uses REPLACE so the newest position is always what gets drained, and
         * requires connectivity so a queued attempt waits rather than burning
         * retries while offline.
         */
        fun enqueue(context: Context) {
            val request = OneTimeWorkRequestBuilder<ProgressSyncWorker>()
                .setConstraints(
                    Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build(),
                )
                .setBackoffCriteria(BackoffPolicy.EXPONENTIAL, 30, TimeUnit.SECONDS)
                .build()
            WorkManager.getInstance(context)
                .enqueueUniqueWork(UNIQUE_WORK, ExistingWorkPolicy.REPLACE, request)
        }
    }
}
