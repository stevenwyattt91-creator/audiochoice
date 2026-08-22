package com.audiochoice.mobile.beta

import android.os.Build
import com.audiochoice.mobile.BuildConfig
import com.audiochoice.mobile.player.PlayerUiState

object BetaDiagnostics {
    fun text(state: PlayerUiState): String {
        val book = state.book
        val positionSeconds = state.positionMs.coerceAtLeast(0) / 1000.0
        val chapter = state.chapters.lastOrNull { it.startSeconds <= positionSeconds }
        val event = state.scanEvents.firstOrNull {
            positionSeconds >= it.startTime && positionSeconds < it.endTime
        }
        return """
            AudioChoice Android Beta
            Version: ${BuildConfig.VERSION_NAME}
            Build: ${BuildConfig.VERSION_CODE}
            Android: ${Build.VERSION.RELEASE} (API ${Build.VERSION.SDK_INT})
            Device: ${Build.MANUFACTURER} ${Build.MODEL}

            Book: ${book?.title ?: "none"}
            Edition: ${book?.fingerprint?.editionType ?: "none"}
            Part: ${book?.fingerprint?.partNumber ?: "none"}
            Chapter: ${chapter?.title ?: "none"}
            Playback Timestamp: ${format(positionSeconds)}

            Filter Profile Version: ${state.scannerVersion ?: "none"}
            Current Filter Event ID: ${event?.id ?: "none"}
            Filter Category: ${event?.categoryID ?: "none"}
            Filter Start: ${event?.startTime?.let(::format) ?: "none"}
            Filter End: ${event?.endTime?.let(::format) ?: "none"}

            Audiobook Fingerprint: ${book?.fingerprint?.sha256?.take(16) ?: "none"}
        """.trimIndent()
    }

    fun feedbackUrl(baseUrl: String, state: PlayerUiState): String {
        if (baseUrl.startsWith("REPLACE_")) return baseUrl
        val book = state.book
        val positionSeconds = state.positionMs.coerceAtLeast(0) / 1000.0
        val chapter = state.chapters.lastOrNull { it.startSeconds <= positionSeconds }
        val event = state.scanEvents.firstOrNull { positionSeconds in it.startTime..<it.endTime }
        val values = linkedMapOf(
            "app_version" to BuildConfig.VERSION_NAME,
            "build" to BuildConfig.VERSION_CODE.toString(),
            "android" to "${Build.VERSION.RELEASE} (API ${Build.VERSION.SDK_INT})",
            "device" to "${Build.MANUFACTURER} ${Build.MODEL}",
            "book" to book?.title,
            "edition" to book?.fingerprint?.editionType,
            "part" to book?.fingerprint?.partNumber?.toString(),
            "chapter" to chapter?.title,
            "timestamp" to format(positionSeconds),
            "filter_profile_version" to state.scannerVersion,
            "filter_event_id" to event?.id,
            "filter_category" to event?.categoryID,
            "filter_start" to event?.startTime?.let(::format),
            "filter_end" to event?.endTime?.let(::format),
        ).filterValues { !it.isNullOrBlank() }
        if (values.isEmpty()) return baseUrl
        val separator = if ('?' in baseUrl) '&' else '?'
        return baseUrl + separator + values.entries.joinToString("&") { (key, value) ->
            "${encode(key)}=${encode(requireNotNull(value))}"
        }
    }

    private fun encode(value: String) = java.net.URLEncoder.encode(value, Charsets.UTF_8.name())
    private fun format(seconds: Double): String {
        val total = seconds.coerceAtLeast(0.0).toLong()
        return "%d:%02d:%02d".format(total / 3600, (total % 3600) / 60, total % 60)
    }
}
