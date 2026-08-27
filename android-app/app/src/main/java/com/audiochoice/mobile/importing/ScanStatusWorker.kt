package com.audiochoice.mobile.importing

import android.content.Context
import androidx.work.BackoffPolicy
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import com.audiochoice.contracts.CloudScanStatus
import com.audiochoice.mobile.data.ApiException
import com.audiochoice.mobile.data.AudioChoiceApi
import com.audiochoice.mobile.data.SessionStore
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.flow.first
import kotlinx.serialization.json.Json

class ScanStatusWorker(
    applicationContext: Context,
    parameters: WorkerParameters,
) : CoroutineWorker(applicationContext, parameters) {
    override suspend fun doWork(): Result {
        val activeScan = ActiveScanStore(applicationContext).load() ?: return Result.success()
        val json = Json { ignoreUnknownKeys = true; encodeDefaults = true }
        val session = SessionStore(applicationContext, json).session.first() ?: return Result.retry()
        val response = runCatching {
            AudioChoiceApi(json).scanJob(session.accessToken, activeScan.scanID)
        }.getOrElse { error ->
            // Retrying a rejected session with the same token can never succeed,
            // so stop instead of polling forever on a linear 30s backoff.
            if (error is ApiException && error.statusCode == 401) return Result.failure()
            return Result.retry()
        }

        return when (response.status) {
            CloudScanStatus.AVAILABLE, CloudScanStatus.COMPLETED -> {
                ScanCompletionNotifier(applicationContext).notifyReady(activeScan.fileName)
                Result.success()
            }
            CloudScanStatus.FAILED -> Result.failure()
            else -> Result.retry()
        }
    }

    companion object {
        private const val UNIQUE_WORK = "audiochoice-active-scan-status"

        fun schedule(context: Context) {
            val request = OneTimeWorkRequestBuilder<ScanStatusWorker>()
                .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
                .setBackoffCriteria(BackoffPolicy.LINEAR, 30, TimeUnit.SECONDS)
                .build()
            WorkManager.getInstance(context).enqueueUniqueWork(UNIQUE_WORK, ExistingWorkPolicy.KEEP, request)
        }

        fun cancel(context: Context) {
            WorkManager.getInstance(context).cancelUniqueWork(UNIQUE_WORK)
        }
    }
}
