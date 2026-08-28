package com.audiochoice.mobile.data

import com.audiochoice.contracts.BookFingerprint
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/** What a listener is telling us the filter got wrong. */
@Serializable
enum class FilterReportKind {
    /** Something played that should have been removed. */
    @SerialName("missedContent")
    MISSED_CONTENT,

    /** Something was removed that should have played. */
    @SerialName("wronglyFiltered")
    WRONGLY_FILTERED,
}

/**
 * A report that filtering was wrong at a particular moment.
 *
 * Carries a position and nothing about what was heard: no audio, no transcript text, no
 * words. The server already holds the transcript for this edition, so a timestamp is enough
 * to find the passage, and sending the content would undo the promise that a listener's audio
 * never leaves their device.
 */
@Serializable
data class FilterReportRequest(
    val fingerprint: BookFingerprint,
    val kind: FilterReportKind,
    val positionSeconds: Double,
    /**
     * How much audio before the tap this covers. A listener reacts, finds the button and
     * taps, by which time the passage is already behind them.
     */
    val windowSeconds: Double? = null,
    /** Which scan produced the result, so a fixed scanner can be told from a bad match. */
    val scannerVersion: String? = null,
    /** Set when reporting a specific skip, which is what makes over-filtering actionable. */
    @SerialName("scanEventID")
    val scanEventID: String? = null,
    @SerialName("categoryID")
    val categoryID: String? = null,
)

/** Only the identifier is read back; nothing in the app depends on the stored report. */
@Serializable
data class FilterReportAcknowledgement(val id: String)

/**
 * Turns a moment in a book into a report.
 *
 * Mirrors FilterReportComposer on iOS, so a report means the same thing whichever app it came
 * from and triage does not have to know which.
 */
object FilterReportComposer {
    /**
     * How much audio before the tap a report covers.
     *
     * A listener has to hear the passage, realise it should not have played, find the button
     * and tap. Twenty seconds covers that without sweeping in so much that triage cannot tell
     * what was meant.
     */
    const val LOOK_BACK_SECONDS: Double = 20.0

    /** Longer than this describes the book rather than a moment in it. */
    const val MAXIMUM_WINDOW_SECONDS: Double = 120.0

    fun missedContent(
        fingerprint: BookFingerprint,
        positionSeconds: Double,
        scannerVersion: String?,
        categoryID: String? = null,
    ): FilterReportRequest = FilterReportRequest(
        fingerprint = fingerprint,
        kind = FilterReportKind.MISSED_CONTENT,
        positionSeconds = positionSeconds.coerceAtLeast(0.0),
        windowSeconds = LOOK_BACK_SECONDS,
        scannerVersion = scannerVersion,
        categoryID = categoryID,
    )

    /**
     * A report that a skip removed something it should not have.
     *
     * Carries the event, which is what makes this actionable: it names the control that fired
     * rather than leaving a timestamp to be matched back to one.
     */
    fun wronglyFiltered(
        fingerprint: BookFingerprint,
        eventID: String?,
        categoryID: String?,
        startSeconds: Double,
        endSeconds: Double,
        scannerVersion: String?,
    ): FilterReportRequest {
        val span = (endSeconds - startSeconds).coerceAtLeast(1.0)
        return FilterReportRequest(
            fingerprint = fingerprint,
            kind = FilterReportKind.WRONGLY_FILTERED,
            positionSeconds = startSeconds.coerceAtLeast(0.0),
            windowSeconds = minOf(span, MAXIMUM_WINDOW_SECONDS),
            scannerVersion = scannerVersion,
            scanEventID = eventID,
            categoryID = categoryID,
        )
    }
}
