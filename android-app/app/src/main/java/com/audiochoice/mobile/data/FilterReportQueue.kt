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
     * Only a report the server can never accept is discarded; anything else is worth another
     * attempt later.
     */
    suspend fun flush(accessToken: String) {
        val pending = load()
        if (pending.isEmpty()) return
        val remaining = mutableListOf<FilterReportRequest>()
        for (report in pending) {
            val outcome = runCatching { api.reportFilter(accessToken, report) }
            outcome.onFailure { error ->
                val permanent = error is ApiException && isPermanentRefusal(error.statusCode)
                if (!permanent) remaining.add(report)
            }
        }
        save(remaining)
    }

    /**
     * Whether a status means this report can never succeed.
     *
     * Only a malformed report qualifies. One case matters in particular: an app released ahead
     * of the server gets 404 from an endpoint that does not exist yet, and treating that as a
     * refusal would quietly discard every report made before the server caught up -- exactly
     * the reports from the listeners who tried first.
     *
     * 400 and 422 are the server saying the report itself is wrong. 401 and 403 can pass once
     * the session is renewed, 404 and 501 once the endpoint exists, 429 once the limit resets.
     */
    private fun isPermanentRefusal(statusCode: Int): Boolean =
        statusCode == 400 || statusCode == 422

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
