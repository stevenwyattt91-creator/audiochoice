package com.audiochoice.mobile.data

import android.content.Context
import kotlinx.serialization.json.Json

/**
 * Holds filter reports until they reach the server.
 *
 * Queuing is not optional. The moment someone most wants to report a missed passage is in a
 * car, on a run, or anywhere else with no signal, and a report that failed to upload is a
 * mistake nobody ever hears about. Reports are written to disk first and sent afterwards, so
 * the tap always succeeds from the listener's point of view.
 *
 * Ordering does not matter and neither does immediacy: each report is an independent
 * observation stamped with the moment it describes, not the moment it was sent.
 */
class FilterReportQueue(
    context: Context,
    private val json: Json,
    private val api: AudioChoiceApi,
) {
    private val preferences =
        context.getSharedPreferences(PREFERENCES_NAME, Context.MODE_PRIVATE)

    /** Records a report. Never throws: the listener has already moved on. */
    fun enqueue(report: FilterReportRequest) {
        val pending = load().toMutableList()
        pending.add(report)
        // Stops a stuck queue growing without bound. The oldest go first: a recent report is
        // likelier to still be worth acting on, and by this point the same problem has almost
        // certainly been reported already.
        while (pending.size > MAXIMUM_PENDING) pending.removeAt(0)
        save(pending)
    }

    /**
     * Sends everything queued, keeping whatever fails.
     *
     * A refused report is discarded rather than retried forever. The server refuses one only
     * when it is malformed, and re-sending it would occupy the queue indefinitely while
     * achieving nothing.
     */
    suspend fun flush(accessToken: String) {
        val pending = load()
        if (pending.isEmpty()) return
        val remaining = mutableListOf<FilterReportRequest>()
        for (report in pending) {
            val outcome = runCatching { api.reportFilter(accessToken, report) }
            outcome.onFailure { error ->
                val refused = error is ApiException && error.statusCode in 400..499
                if (!refused) remaining.add(report)
            }
        }
        save(remaining)
    }

    fun pendingCount(): Int = load().size

    private fun load(): List<FilterReportRequest> {
        val stored = preferences.getString(PENDING_KEY, null) ?: return emptyList()
        return runCatching {
            json.decodeFromString<List<FilterReportRequest>>(stored)
        }.getOrDefault(emptyList())
    }

    private fun save(values: List<FilterReportRequest>) {
        preferences.edit()
            .putString(PENDING_KEY, json.encodeToString(values))
            .apply()
    }

    private companion object {
        const val PREFERENCES_NAME = "audiochoice_filter_reports"
        const val PENDING_KEY = "pending_v1"
        const val MAXIMUM_PENDING = 200
    }
}
