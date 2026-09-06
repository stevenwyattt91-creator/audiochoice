package com.audiochoice.mobile.data

import com.audiochoice.contracts.BookFingerprint
import com.audiochoice.contracts.ScanEvent
import kotlinx.serialization.EncodeDefault
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
    /**
     * What [positionSeconds] measures: seconds of audio, or a character offset into a narrated
     * book's text.
     *
     * Additive, optional, and defaulted to null so an imported audiobook's request body is
     * byte-identical to what every shipped client already sends. Renaming `positionSeconds`
     * would have been cleaner and would have broken the iOS client, the Android release build
     * and the admin triage views at once.
     *
     * Null means seconds. Triage reading a character offset as a timestamp is the failure this
     * field exists to prevent, which is why the server constrains it to two values rather than
     * accepting free text.
     *
     * [EncodeDefault] with mode NEVER is load-bearing, not tidiness. The app configures its
     * `Json` with `encodeDefaults = true`, so without this a null would still be written as
     * `"positionUnit":null` -- and "optional field with a default" would silently stop being
     * wire-compatible. An audiobook's request body has to stay byte-identical to what shipped
     * clients send, and this is what keeps it that way.
     */
    @EncodeDefault(EncodeDefault.Mode.NEVER)
    @SerialName("positionUnit")
    val positionUnit: String? = null,
)

/** Only the identifier is read back; nothing in the app depends on the stored report. */
@Serializable
data class FilterReportAcknowledgement(val id: String)

/**
 * The taxonomy's six top-level categories, for a report's own category picker.
 *
 * Independent of [PlaybackFilterTaxonomy][com.audiochoice.mobile.player.PlaybackFilterTaxonomy],
 * which only lists categories a book's own scan actually produced. A report's picker has to
 * offer every category regardless of what this book was found to contain -- the whole point
 * is telling triage about something the scan missed. Mirrors iOS's IOSContentTaxonomy so the
 * two apps' pickers name the same six things with the same identifiers.
 */
enum class FilterReportCategory(val categoryID: String, val label: String) {
    SEXUAL_CONTENT("10000000-0000-0000-0000-000000000001", "Sexual Content"),
    PROFANITY("20000000-0000-0000-0000-000000000001", "Profanity"),
    VIOLENCE("30000000-0000-0000-0000-000000000001", "Violence"),
    DRUGS_AND_ALCOHOL("40000000-0000-0000-0000-000000000001", "Drugs & Alcohol"),
    BLASPHEMY("50000000-0000-0000-0000-000000000001", "Blasphemy"),
    SELF_HARM("60000000-0000-0000-0000-000000000001", "Self-Harm & Suicide"),
}

/**
 * How far back a refined report reaches, as a labelled choice rather than free entry.
 *
 * A listener picking this has no reason to know the server's own limit, so the choices stop
 * at it ([FilterReportComposer.MAXIMUM_WINDOW_SECONDS]) rather than offering one that would be
 * silently clamped.
 */
enum class FilterReportTimeframe(val seconds: Double, val label: String) {
    JUST_THIS_MOMENT(FilterReportComposer.LOOK_BACK_SECONDS, "Just this moment (20s)"),
    HALF_MINUTE(30.0, "Last 30 seconds"),
    ONE_MINUTE(60.0, "Last minute"),
    TWO_MINUTES(FilterReportComposer.MAXIMUM_WINDOW_SECONDS, "Last 2 minutes"),
}

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
        windowSeconds: Double = LOOK_BACK_SECONDS,
    ): FilterReportRequest = FilterReportRequest(
        fingerprint = fingerprint,
        kind = FilterReportKind.MISSED_CONTENT,
        positionSeconds = positionSeconds.coerceAtLeast(0.0),
        windowSeconds = windowSeconds.coerceIn(1.0, MAXIMUM_WINDOW_SECONDS),
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
