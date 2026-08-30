package com.audiochoice.mobile.narration

import com.audiochoice.contracts.BookFingerprint
import com.audiochoice.contracts.NarrationTextScanRequest
import com.audiochoice.contracts.NarrationTextScanResponse
import com.audiochoice.contracts.ScanEvent
import com.audiochoice.mobile.data.ApiException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.serialization.Serializable

/**
 * A text scan as it is kept on the device.
 *
 * [bookTextCharacters] is the length of the text the offsets were produced against, and it
 * is the only thing that makes a stored scan verifiable later. Book_Text is not kept
 * alongside it, so if the text is re-extracted at a different length there is no way to
 * reinterpret the offsets -- they simply stop applying, and this field is how that is
 * noticed instead of the wrong passages being filtered.
 */
@Serializable
data class StoredTextScan(
    val events: List<ScanEvent>,
    val scannerVersion: String,
    val taxonomyVersion: String,
    val scanDate: String?,
    val bookTextCharacters: Int,
) {
    /** Whether these offsets still index a book of [bookTextLength] characters. */
    fun appliesTo(bookTextLength: Int): Boolean =
        bookTextCharacters == bookTextLength &&
            FilteredRanges.offsetsAreValid(events, bookTextLength)
}

/** What asking for a scan produced. */
sealed interface TextScanOutcome {

    /** Filter results are available, from the network or from disk. */
    data class Completed(val scan: StoredTextScan, val fromCache: Boolean) : TextScanOutcome

    /**
     * The listener has not agreed to their book's text leaving the device.
     *
     * No request was made and none will be. Distinct from [Unavailable] because nothing
     * failed and retrying changes nothing: what is needed is the listener's decision.
     */
    data object AcknowledgementRequired : TextScanOutcome

    /**
     * No filter results, after exhausting the retries.
     *
     * The book stays unrendered until either a later scan succeeds or the listener chooses
     * to continue without filtering. Reported rather than thrown, because "your filters
     * aren't ready" is a state the library and detail surfaces have to show, not an error to
     * swallow at a call site.
     */
    data class Unavailable(val reason: Reason, val attempts: Int) : TextScanOutcome {
        enum class Reason {
            /** Network or server trouble that another attempt might get past. */
            TRANSIENT,

            /** The server refused the request outright. Retrying sends the same thing. */
            REFUSED,

            /** The server is reachable but has text scanning switched off. */
            UNSUPPORTED,

            /**
             * Offsets that cannot index this book.
             *
             * The whole batch is discarded. See [FilteredRanges.offsetsAreValid].
             */
            COORDINATE_MISMATCH,
        }
    }
}

/**
 * Gets filter results for a narrated book, then works offline.
 *
 * The outbound call is a constructor parameter rather than an `AudioChoiceApi` reference, so
 * the retry and validation rules here are testable without a network. `AudioChoiceApi` binds
 * to it at the call site.
 *
 * Book_Text is passed in per call and never held as state. This class writes events, a
 * scanner version and a length; it has no method that could write the text, which is a
 * stronger guarantee than remembering not to.
 */
class TextScanClient(
    private val store: NarrationStore,
    private val scan: suspend (NarrationTextScanRequest) -> NarrationTextScanResponse,
    private val acknowledgement: suspend () -> TextScanAcknowledgementRecord?,
    /** Injected so tests exercise the retry ladder without waiting through it. */
    private val pause: suspend (Long) -> Unit = { delay(it) },
) {

    /**
     * Get filter results for this book, reusing a stored scan when one still applies.
     *
     * The order matters. The cache is consulted before the acknowledgement, because a book
     * already scanned needs no further permission and asking again would be pestering
     * someone about something that has already happened.
     */
    suspend fun ensureScan(
        fingerprint: BookFingerprint,
        bookText: String,
        language: String? = null,
    ): TextScanOutcome {
        val cached = store.textScan(fingerprint.sha256)
        if (cached != null && cached.appliesTo(bookText.length)) {
            return TextScanOutcome.Completed(cached, fromCache = true)
        }

        // A stored scan that no longer applies is worse than none: its offsets point into a
        // coordinate space that has gone. Drop it before asking for a replacement, so a
        // failed re-scan cannot leave the old one behind to be picked up later.
        if (cached != null) store.deleteTextScan(fingerprint.sha256)

        if (!TextScanAcknowledgement.isCurrent(acknowledgement())) {
            return TextScanOutcome.AcknowledgementRequired
        }

        val request = NarrationTextScanRequest(fingerprint, bookText, language)
        var attempts = 0
        var lastTransient: TextScanOutcome.Unavailable.Reason =
            TextScanOutcome.Unavailable.Reason.TRANSIENT

        while (attempts < MAXIMUM_ATTEMPTS) {
            attempts += 1
            val response = try {
                scan(request)
            } catch (cancellation: CancellationException) {
                // Must propagate. Swallowing it would keep a cancelled import retrying in
                // the background against a book the listener has walked away from.
                throw cancellation
            } catch (failure: ApiException) {
                val reason = reasonFor(failure.statusCode)
                    ?: return TextScanOutcome.Unavailable(
                        TextScanOutcome.Unavailable.Reason.REFUSED, attempts,
                    )
                lastTransient = reason
                if (attempts < MAXIMUM_ATTEMPTS) pause(delayFor(attempts))
                continue
            } catch (failure: Throwable) {
                lastTransient = TextScanOutcome.Unavailable.Reason.TRANSIENT
                if (attempts < MAXIMUM_ATTEMPTS) pause(delayFor(attempts))
                continue
            }

            val validated = validate(response, bookText.length)
                ?: return TextScanOutcome.Unavailable(
                    // Not retried. A coordinate-space disagreement is a version skew between
                    // this build and the server, and asking again produces the same answer.
                    TextScanOutcome.Unavailable.Reason.COORDINATE_MISMATCH, attempts,
                )

            store.saveTextScan(fingerprint.sha256, validated)
            return TextScanOutcome.Completed(validated, fromCache = false)
        }

        return TextScanOutcome.Unavailable(lastTransient, attempts)
    }

    /**
     * Accept a response only if every offset in it indexes this book.
     *
     * All or nothing, and the reasoning is in [FilteredRanges.offsetsAreValid]: one
     * out-of-range offset means the two sides disagree about the coordinate space, and in
     * that state no event in the batch is trustworthy. A half-applied filter is worse than
     * an absent one, because the listener is told filtering is on.
     *
     * The length is checked as well as the offsets. Every offset can be within range while
     * still belonging to a different, shorter text -- in which case they are all quietly
     * pointing at the wrong words.
     */
    private fun validate(
        response: NarrationTextScanResponse,
        bookTextLength: Int,
    ): StoredTextScan? {
        if (response.bookTextCharacters != bookTextLength) return null
        if (!FilteredRanges.offsetsAreValid(response.events, bookTextLength)) return null
        return StoredTextScan(
            events = response.events,
            scannerVersion = response.scannerVersion,
            taxonomyVersion = response.taxonomyVersion,
            scanDate = response.scanDate,
            bookTextCharacters = response.bookTextCharacters,
        )
    }

    companion object {
        /**
         * Three attempts, then the listener is told.
         *
         * More would keep an import screen spinning past the point anyone waits, and this
         * failure has a good resting state: the book is imported and readable, only its
         * filter results are missing, and they can be fetched later.
         */
        const val MAXIMUM_ATTEMPTS = 3

        /**
         * Increasing, and long enough to be worth waiting for.
         *
         * A text scan fails slowly -- a busy classifier, a cold server, a long book against
         * the budget -- so retrying in a few hundred milliseconds would just fail three
         * times in a row at the same moment. Two seconds, then four.
         */
        fun delayFor(attempt: Int): Long = 2_000L shl (attempt - 1)

        /**
         * Whether a status is worth another attempt, or `null` when it is a flat refusal.
         *
         * Follows the convention already set by the filter-report queue: treat only what the
         * server is definitely rejecting as permanent. A 404 is retryable here on purpose --
         * it is what this endpoint returns when narration is switched off server-side, and
         * this build may well be running ahead of the servers, so a listener retrying
         * tomorrow should succeed rather than be told their book was refused.
         */
        fun reasonFor(statusCode: Int): TextScanOutcome.Unavailable.Reason? = when (statusCode) {
            400, 401, 403, 413, 422 -> null
            404, 501 -> TextScanOutcome.Unavailable.Reason.UNSUPPORTED
            else -> TextScanOutcome.Unavailable.Reason.TRANSIENT
        }
    }
}
