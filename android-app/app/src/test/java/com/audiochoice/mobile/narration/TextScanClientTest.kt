package com.audiochoice.mobile.narration

import com.audiochoice.contracts.BookFingerprint
import com.audiochoice.contracts.NarrationTextScanRequest
import com.audiochoice.contracts.NarrationTextScanResponse
import com.audiochoice.contracts.ScanEvent
import com.audiochoice.mobile.data.ApiException
import com.audiochoice.mobile.player.PlaybackFilterTaxonomy
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class TextScanClientTest {

    @get:Rule
    val temporaryFolder = TemporaryFolder()

    // region acknowledgement gate

    /**
     * Nothing leaves the device before the listener agrees. The assertion that matters is
     * not the returned state but that the outbound call was never made.
     */
    @Test
    fun `no request is made while no acknowledgement is recorded`(): Unit = runBlocking {
        val calls = mutableListOf<NarrationTextScanRequest>()
        val client = client(
            respond = { request -> calls += request; response(bookTextCharacters = BOOK_TEXT.length) },
            hasAcknowledged = false,
        )

        val outcome = client.ensureScan(FINGERPRINT, BOOK_TEXT)

        assertEquals(TextScanOutcome.AcknowledgementRequired, outcome)
        assertTrue("the book's text was sent before the listener agreed", calls.isEmpty())
        assertNull(store().textScan(FINGERPRINT.sha256))
    }

    /**
     * A version bump means the listener agreed to a different set of recipients from the one
     * now in force, so they are asked again.
     */
    @Test
    fun `a stale acknowledgement version blocks the request`(): Unit = runBlocking {
        val calls = mutableListOf<NarrationTextScanRequest>()
        val client = TextScanClient(
            store = store(),
            scan = { request -> calls += request; response(bookTextCharacters = BOOK_TEXT.length) },
            acknowledgement = {
                TextScanAcknowledgementRecord("0", "an older statement", 1L)
            },
            pause = {},
        )

        assertEquals(
            TextScanOutcome.AcknowledgementRequired,
            client.ensureScan(FINGERPRINT, BOOK_TEXT),
        )
        assertTrue(calls.isEmpty())
    }

    /**
     * A book already scanned needs no further permission. Asking again would be pestering
     * someone about something that has already happened.
     */
    @Test
    fun `a cached scan is returned without an acknowledgement`(): Unit = runBlocking {
        val store = store()
        val stored = StoredTextScan(
            events = listOf(event(10, 20)),
            scannerVersion = "v1",
            taxonomyVersion = "2.0",
            scanDate = null,
            bookTextCharacters = BOOK_TEXT.length,
        )
        store.saveTextScan(FINGERPRINT.sha256, stored)

        var called = false
        val client = TextScanClient(
            store = store,
            scan = { called = true; response(bookTextCharacters = BOOK_TEXT.length) },
            acknowledgement = { null },
            pause = {},
        )

        val outcome = client.ensureScan(FINGERPRINT, BOOK_TEXT)

        assertTrue(outcome is TextScanOutcome.Completed)
        assertTrue((outcome as TextScanOutcome.Completed).fromCache)
        assertFalse("a cached scan still hit the network", called)
    }

    // endregion

    // region offline reuse

    /**
     * The scan is requested once per book. A second call with unchanged text works entirely
     * from disk, which is what makes the feature usable offline.
     */
    @Test
    fun `a second call with unchanged text makes no request`(): Unit = runBlocking {
        val store = store()
        var calls = 0
        val client = TextScanClient(
            store = store,
            scan = { calls += 1; response(bookTextCharacters = BOOK_TEXT.length) },
            acknowledgement = { acknowledged() },
            pause = {},
        )

        val first = client.ensureScan(FINGERPRINT, BOOK_TEXT)
        val second = client.ensureScan(FINGERPRINT, BOOK_TEXT)

        assertTrue(first is TextScanOutcome.Completed)
        assertFalse((first as TextScanOutcome.Completed).fromCache)
        assertTrue(second is TextScanOutcome.Completed)
        assertTrue((second as TextScanOutcome.Completed).fromCache)
        assertEquals("the scan was requested more than once per book", 1, calls)
    }

    /**
     * Text of a different length means every stored offset belongs to a coordinate space
     * that no longer exists. The old scan must be discarded rather than reinterpreted, and
     * discarded *before* the replacement is attempted -- otherwise a failed re-scan would
     * leave it on disk to be picked up and applied to the wrong words later.
     */
    @Test
    fun `a stored scan for different text is discarded even when the re-scan fails`(): Unit =
        runBlocking {
            val store = store()
            store.saveTextScan(
                FINGERPRINT.sha256,
                StoredTextScan(
                    events = listOf(event(10, 20)),
                    scannerVersion = "v1",
                    taxonomyVersion = "2.0",
                    scanDate = null,
                    bookTextCharacters = 40,
                ),
            )
            val client = TextScanClient(
                store = store,
                scan = { throw ApiException(500, "server trouble") },
                acknowledgement = { acknowledged() },
                pause = {},
            )

            val outcome = client.ensureScan(FINGERPRINT, BOOK_TEXT)

            assertTrue(outcome is TextScanOutcome.Unavailable)
            assertNull(
                "a scan for text of a different length survived a failed replacement",
                store.textScan(FINGERPRINT.sha256),
            )
        }

    // endregion

    // region batch validation

    /**
     * One bad offset discards the whole response.
     *
     * Not a per-event filter, and deliberately so: an out-of-range offset means this build
     * and the server disagree about what the numbers mean, and in that state the events that
     * happen to be in range are not trustworthy either. A half-applied filter is worse than
     * none, because the listener is told filtering is on.
     */
    @Test
    fun `one out-of-range offset invalidates the entire batch`(): Unit = runBlocking {
        val store = store()
        val client = TextScanClient(
            store = store,
            scan = {
                response(
                    events = listOf(
                        event(0, 10),
                        event(20, 30),
                        event(BOOK_TEXT.length - 1, BOOK_TEXT.length + 5),
                    ),
                    bookTextCharacters = BOOK_TEXT.length,
                )
            },
            acknowledgement = { acknowledged() },
            pause = {},
        )

        val outcome = client.ensureScan(FINGERPRINT, BOOK_TEXT)

        assertEquals(
            TextScanOutcome.Unavailable.Reason.COORDINATE_MISMATCH,
            (outcome as TextScanOutcome.Unavailable).reason,
        )
        assertNull("an invalid batch was persisted", store.textScan(FINGERPRINT.sha256))
    }

    @Test
    fun `an inverted range invalidates the entire batch`(): Unit = runBlocking {
        val client = client(
            respond = {
                response(
                    events = listOf(event(0, 10), event(30, 20)),
                    bookTextCharacters = BOOK_TEXT.length,
                )
            },
        )
        val outcome = client.ensureScan(FINGERPRINT, BOOK_TEXT)
        assertEquals(
            TextScanOutcome.Unavailable.Reason.COORDINATE_MISMATCH,
            (outcome as TextScanOutcome.Unavailable).reason,
        )
    }

    /**
     * A fractional offset is a value that was produced as a time rather than as an index
     * into a string. Rejecting it is what stops a seconds-shaped number being applied to
     * characters.
     */
    @Test
    fun `a fractional offset invalidates the entire batch`(): Unit = runBlocking {
        val client = client(
            respond = {
                response(
                    events = listOf(ScanEvent(
                        id = "1", startTime = 10.5, endTime = 20.0,
                        categoryID = CATEGORY, groupID = GROUP, eventID = EVENT,
                        confidence = 1.0, stableKey = "k", safeDescription = "Profanity detected",
                    )),
                    bookTextCharacters = BOOK_TEXT.length,
                )
            },
        )
        assertEquals(
            TextScanOutcome.Unavailable.Reason.COORDINATE_MISMATCH,
            (client.ensureScan(FINGERPRINT, BOOK_TEXT) as TextScanOutcome.Unavailable).reason,
        )
    }

    /**
     * Every offset can be in range and still belong to a different, shorter text. Checking
     * the length as well as the offsets is what catches that, because otherwise the events
     * are quietly pointing at the wrong words.
     */
    @Test
    fun `a length disagreement invalidates the batch even when every offset is in range`(): Unit =
        runBlocking {
            val client = client(
                respond = {
                    response(
                        events = listOf(event(0, 10)),
                        bookTextCharacters = BOOK_TEXT.length - 12,
                    )
                },
            )
            assertEquals(
                TextScanOutcome.Unavailable.Reason.COORDINATE_MISMATCH,
                (client.ensureScan(FINGERPRINT, BOOK_TEXT) as TextScanOutcome.Unavailable).reason,
            )
        }

    /** An empty event list is a real answer: this book has nothing to filter. */
    @Test
    fun `an empty event list is a completed scan`(): Unit = runBlocking {
        val client = client(
            respond = { response(events = emptyList(), bookTextCharacters = BOOK_TEXT.length) },
        )
        val outcome = client.ensureScan(FINGERPRINT, BOOK_TEXT)
        assertTrue(outcome is TextScanOutcome.Completed)
        assertTrue((outcome as TextScanOutcome.Completed).scan.events.isEmpty())
    }

    // endregion

    // region retry

    @Test
    fun `a transient failure is retried three times then reported`(): Unit = runBlocking {
        var calls = 0
        val delays = mutableListOf<Long>()
        val client = TextScanClient(
            store = store(),
            scan = { calls += 1; throw ApiException(504, "took too long") },
            acknowledgement = { acknowledged() },
            pause = { delays += it },
        )

        val outcome = client.ensureScan(FINGERPRINT, BOOK_TEXT)

        assertEquals(TextScanClient.MAXIMUM_ATTEMPTS, calls)
        assertEquals(TextScanOutcome.Unavailable.Reason.TRANSIENT,
            (outcome as TextScanOutcome.Unavailable).reason)
        assertEquals(TextScanClient.MAXIMUM_ATTEMPTS, outcome.attempts)
        // Waited between attempts but not after the last one: nobody is waiting on a delay
        // that precedes giving up.
        assertEquals(listOf(2_000L, 4_000L), delays)
    }

    @Test
    fun `a retry that succeeds is a completed scan`(): Unit = runBlocking {
        var calls = 0
        val client = TextScanClient(
            store = store(),
            scan = {
                calls += 1
                if (calls < 3) throw java.io.IOException("network dropped")
                response(events = listOf(event(0, 10)), bookTextCharacters = BOOK_TEXT.length)
            },
            acknowledgement = { acknowledged() },
            pause = {},
        )

        val outcome = client.ensureScan(FINGERPRINT, BOOK_TEXT)

        assertTrue(outcome is TextScanOutcome.Completed)
        assertEquals(3, calls)
        assertNotNull("a scan that succeeded on retry was not persisted",
            store().textScan(FINGERPRINT.sha256))
    }

    /**
     * A refusal is not retried. Sending the same request again produces the same refusal,
     * and three rounds of it only delay telling the listener.
     */
    @Test
    fun `a refusal is not retried`(): Unit = runBlocking {
        var calls = 0
        val client = TextScanClient(
            store = store(),
            scan = { calls += 1; throw ApiException(400, "the book text is empty or too large") },
            acknowledgement = { acknowledged() },
            pause = {},
        )

        val outcome = client.ensureScan(FINGERPRINT, BOOK_TEXT)

        assertEquals(1, calls)
        assertEquals(TextScanOutcome.Unavailable.Reason.REFUSED,
            (outcome as TextScanOutcome.Unavailable).reason)
    }

    /**
     * A coordinate mismatch is not retried either, for the same reason: it is a version skew
     * between this build and the server, so the next attempt returns the same numbers.
     */
    @Test
    fun `a coordinate mismatch is not retried`(): Unit = runBlocking {
        var calls = 0
        val client = TextScanClient(
            store = store(),
            scan = {
                calls += 1
                response(events = listOf(event(0, 99_999)), bookTextCharacters = BOOK_TEXT.length)
            },
            acknowledgement = { acknowledged() },
            pause = {},
        )

        client.ensureScan(FINGERPRINT, BOOK_TEXT)

        assertEquals(1, calls)
    }

    /**
     * 404 is what this endpoint returns when narration is switched off server-side. The app
     * may ship ahead of the servers, so it is reported as unsupported and retried rather
     * than recorded as a refusal that would stick.
     */
    @Test
    fun `narration switched off server-side is unsupported rather than refused`(): Unit =
        runBlocking {
            val client = TextScanClient(
                store = store(),
                scan = { throw ApiException(404, "not found") },
                acknowledgement = { acknowledged() },
                pause = {},
            )

            val outcome = client.ensureScan(FINGERPRINT, BOOK_TEXT)

            assertEquals(TextScanOutcome.Unavailable.Reason.UNSUPPORTED,
                (outcome as TextScanOutcome.Unavailable).reason)
        }

    @Test
    fun `retry classification covers the statuses this endpoint returns`() {
        // Refusals: retrying sends the identical request.
        listOf(400, 401, 403, 413, 422).forEach { status ->
            assertNull("$status should be a flat refusal", TextScanClient.reasonFor(status))
        }
        // Narration off, or a server that predates the endpoint.
        listOf(404, 501).forEach { status ->
            assertEquals(
                TextScanOutcome.Unavailable.Reason.UNSUPPORTED,
                TextScanClient.reasonFor(status),
            )
        }
        // Worth another attempt: the budget expiring, an unregistered pipeline, a wobble.
        listOf(429, 500, 502, 503, 504).forEach { status ->
            assertEquals(
                "$status should be retried",
                TextScanOutcome.Unavailable.Reason.TRANSIENT,
                TextScanClient.reasonFor(status),
            )
        }
        assertEquals(2_000L, TextScanClient.delayFor(1))
        assertEquals(4_000L, TextScanClient.delayFor(2))
    }

    /**
     * Cancellation must escape rather than be caught as a failure, or a cancelled import
     * keeps retrying in the background against a book the listener walked away from.
     */
    @Test
    fun `cancellation propagates instead of being retried`(): Unit = runBlocking {
        var calls = 0
        val client = TextScanClient(
            store = store(),
            scan = { calls += 1; throw kotlinx.coroutines.CancellationException("gone") },
            acknowledgement = { acknowledged() },
            pause = {},
        )

        var propagated = false
        try {
            client.ensureScan(FINGERPRINT, BOOK_TEXT)
        } catch (expected: kotlinx.coroutines.CancellationException) {
            propagated = true
        }

        assertTrue("cancellation was swallowed and retried", propagated)
        assertEquals(1, calls)
    }

    // endregion

    // region the existing filter stack

    /**
     * The whole reason a text scan carries character offsets in a `ScanEvent`'s time fields:
     * a narrated book presents exactly the control tree an imported audiobook does. Same
     * events in, same tree out, with no narration-specific branch in the taxonomy.
     */
    @Test
    fun `narrated events build a real control tree through the unmodified taxonomy`(): Unit =
        runBlocking {
            // Events as the text scan delivers them: offsets in characters, taxonomy
            // identifiers identical to an audio scan's.
            val events = listOf(
                event(0, 10, aggregateKey = "word|damn", aggregateDisplay = "d***"),
                event(120, 130, aggregateKey = "word|damn", aggregateDisplay = "d***"),
                event(400, 460, group = SEXUAL_GROUP, category = SEXUAL_CATEGORY,
                    description = "Characters are in bed together"),
            )
            val client = client(
                respond = { response(events = events, bookTextCharacters = BOOK_TEXT.length) },
            )

            val outcome = client.ensureScan(FINGERPRINT, BOOK_TEXT)
            val stored = (outcome as TextScanOutcome.Completed).scan

            // The tree is built from what actually came back and was persisted, not from a
            // hand-made list, so this fails if the client alters events on the way through.
            val tree = PlaybackFilterTaxonomy.available(stored.events)

            assertEquals(
                "narrated events did not produce the expected parent controls",
                listOf("Profanity", "Sexual Content"),
                tree.map { it.label }.sorted(),
            )
            // Repeated profanity is one control holding both occurrences, exactly as for an
            // audiobook: the listener switches off the word, not each instance of it.
            val profanity = tree.first { it.label == "Profanity" }
            assertEquals(1, profanity.children.sumOf { child -> child.events.size })
            val word = profanity.children.first().events.first()
            assertEquals(2, word.count)
            assertEquals("d***", word.label)
            assertTrue("a repeated word should be an aggregate control", word.aggregate)
            // Three events, two controls: that difference is the taxonomy doing its job.
            assertEquals(2, PlaybackFilterTaxonomy.controlCount(stored.events))
        }

    /**
     * The taxonomy silently drops events whose group it does not recognise, so an event
     * would be enforced by the predicate while being invisible in the UI. This pins the
     * requirement that a text scan emits the same group identifiers an audio scan does.
     */
    @Test
    fun `an unrecognised group identifier would vanish from the control tree`() {
        val recognised = event(0, 10)
        val unrecognised = event(0, 10, group = "99000000-0000-0000-0000-000000000099")

        assertEquals(1, PlaybackFilterTaxonomy.available(listOf(recognised)).size)
        assertTrue(
            "an unknown group silently produced a control, so this guard is not testing it",
            PlaybackFilterTaxonomy.available(listOf(unrecognised)).isEmpty(),
        )
    }

    /**
     * Switching a control off removes that passage from what will be spoken. This is the
     * path that makes filtering on a narrated book stronger than on an audiobook: the
     * passage is never synthesised at all rather than skipped during playback.
     */
    @Test
    fun `disabling a group removes its ranges from the spoken text`() {
        val events = listOf(event(0, 10), event(120, 130))

        val allOn = FilteredRanges.forEnabledEvents(events)
        val profanityOff = FilteredRanges.forEnabledEvents(
            events, disabledGroupIDs = setOf(GROUP.lowercase()),
        )

        assertEquals(2, allOn.size)
        assertTrue("a disabled group still produced ranges to remove", profanityOff.isEmpty())
    }

    // endregion

    // region acknowledgement copy

    /**
     * The statement has to name each category of recipient. "Third parties" would tell a
     * listener nothing they can weigh, which is the entire purpose of asking.
     */
    @Test
    fun `the statement names every recipient of the book's text`() {
        val statement = TextScanAcknowledgement.STATEMENT
        listOf("AudioChoice", "classification", "SageMaker", "Polly").forEach { recipient ->
            assertTrue(
                "the acknowledgement does not name $recipient",
                statement.contains(recipient),
            )
        }
        // The commitments the implementation actually keeps, and the tests actually check.
        assertTrue(statement.contains("never used to train"))
        assertTrue(statement.contains("No copy of the book is kept"))
        // Honest about the on-device voice needing none of it.
        assertTrue(statement.contains("built-in voice"))
    }

    /** Storing the wording, not just its version, is what makes it producible later. */
    @Test
    fun `the recorded acknowledgement keeps the statement it agreed to`() {
        val record = TextScanAcknowledgement.record(acceptedAtMillis = 1_234L)
        assertEquals(TextScanAcknowledgement.VERSION, record.version)
        assertEquals(TextScanAcknowledgement.STATEMENT, record.statement)
        assertEquals(1_234L, record.acceptedAtMillis)
        assertTrue(TextScanAcknowledgement.isCurrent(record))
        assertFalse(TextScanAcknowledgement.isCurrent(record.copy(version = "0")))
        assertFalse(TextScanAcknowledgement.isCurrent(null))
        // Rewording for clarity must not invalidate everyone's agreement.
        assertTrue(TextScanAcknowledgement.isCurrent(record.copy(statement = "reworded")))
    }

    // endregion

    // region generators and fixtures

    private fun store() = NarrationStore(temporaryFolder.root)

    private fun client(
        respond: suspend (NarrationTextScanRequest) -> NarrationTextScanResponse,
        hasAcknowledged: Boolean = true,
    ) = TextScanClient(
        store = store(),
        scan = respond,
        acknowledgement = { if (hasAcknowledged) acknowledged() else null },
        pause = {},
    )

    private fun acknowledged() = TextScanAcknowledgement.record(acceptedAtMillis = 1L)

    private fun response(
        events: List<ScanEvent> = listOf(event(0, 10)),
        bookTextCharacters: Int,
    ) = NarrationTextScanResponse(
        events = events,
        scanDate = null,
        scannerVersion = "text-v1",
        taxonomyVersion = "2.0",
        bookTextCharacters = bookTextCharacters,
    )

    private fun event(
        start: Int,
        end: Int,
        group: String = GROUP,
        category: String = CATEGORY,
        description: String = "Profanity detected",
        aggregateKey: String? = null,
        aggregateDisplay: String? = null,
    ) = ScanEvent(
        id = "$start-$end",
        startTime = start.toDouble(),
        endTime = end.toDouble(),
        categoryID = category,
        groupID = group,
        eventID = EVENT,
        confidence = 1.0,
        stableKey = "stable-$start-$end",
        safeDescription = description,
        aggregateKey = aggregateKey,
        aggregateDisplay = aggregateDisplay,
    )

    private companion object {
        // Mild profanity, in the taxonomy's group-identifier shape. The taxonomy silently
        // drops groups it does not recognise, so a text scan has to speak these exactly.
        const val CATEGORY = "21000000-0000-0000-0000-000000000000"
        const val GROUP = "21000000-0000-0000-0000-000000000001"
        const val EVENT = "21000000-0000-0000-0000-000000000101"
        const val SEXUAL_CATEGORY = "11000000-0000-0000-0000-000000000000"
        const val SEXUAL_GROUP = "11000000-0000-0000-0000-000000000005"

        val FINGERPRINT = BookFingerprint(
            version = 1,
            sha256 = "a".repeat(64),
            fileSize = 1_024,
            duration = null,
            fileType = "epub",
        )

        val BOOK_TEXT = "Chapter One\n\n" + "The quick brown fox jumped over it. ".repeat(20)
    }
}
