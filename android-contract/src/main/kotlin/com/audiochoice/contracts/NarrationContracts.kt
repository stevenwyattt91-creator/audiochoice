package com.audiochoice.contracts

import kotlinx.serialization.Serializable

/**
 * Asks the server to find filterable content in a book's text, for a book with no audiobook.
 *
 * Reuses [BookFingerprint] rather than declaring a narration-specific identity. A book is the
 * same book whether it arrived as audio or as an EPUB, and sharing the fingerprint is what
 * lets one listener's scan serve the next listener who imports the same file.
 *
 * [bookText] is the only field carrying the book itself, and the server holds it for the
 * length of one request. Nothing sends it twice: a scan is looked up by fingerprint first.
 */
@Serializable
data class NarrationTextScanRequest(
    val fingerprint: BookFingerprint,
    val bookText: String,
    val language: String? = null,
) {
    /**
     * Prints the text's length rather than the text.
     *
     * A data class prints every property, so one log line holding the request would write a
     * whole novel into logcat. That is the same disclosure the feature is careful to avoid,
     * arriving by accident, so the safe rendering is the default rather than something each
     * call site has to remember.
     */
    override fun toString(): String =
        "NarrationTextScanRequest(sha256=${fingerprint.sha256}, " +
            "bookTextCharacters=${bookText.length}, language=$language)"
}

/**
 * Filter events for a book's text.
 *
 * The offsets in each [ScanEvent] are **character offsets into the book's text**, carried in
 * the same `startTime`/`endTime` fields an audio scan uses for seconds. That reuse is
 * deliberate: it lets the entire existing filter stack -- the category switches, the
 * per-word profanity grouping, the aggregate descriptions -- work on a narrated book without
 * a parallel implementation. It is also the single most dangerous thing about this contract,
 * because a value from here reaching a seek call would move the listener by tens of hours.
 * Anything reading these events must keep them apart from playback positions.
 *
 * [bookTextCharacters] is what makes a stale scan detectable: if the book's text is no longer
 * this length, the offsets no longer index it, and the events must be discarded rather than
 * applied to the wrong passages.
 */
@Serializable
data class NarrationTextScanResponse(
    val events: List<ScanEvent> = emptyList(),
    val scanDate: String? = null,
    val scannerVersion: String = "",
    val taxonomyVersion: String = "",
    val bookTextCharacters: Int = 0,
)
