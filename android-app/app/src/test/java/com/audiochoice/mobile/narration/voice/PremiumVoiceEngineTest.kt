package com.audiochoice.mobile.narration.voice

import com.audiochoice.contracts.BookFingerprint
import com.audiochoice.contracts.NarrationChapterRequest
import com.audiochoice.contracts.NarrationChapterStatus
import com.audiochoice.contracts.NarrationUnitTiming
import com.audiochoice.mobile.narration.SpokenUnit
import com.audiochoice.mobile.data.ReaderTimingRange
import com.audiochoice.mobile.data.VoiceKind
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File
import java.util.Base64

class PremiumVoiceEngineTest {

    @get:Rule
    val temporaryFolder = TemporaryFolder()

    // region what is sent

    /**
     * Only the text that survived filtering is sent.
     *
     * The strongest privacy property this feature has, and the one most easily lost: units reach an
     * engine with filtered characters already removed, so a passage the listener filtered is not
     * withheld from the request -- it was never in it. Asserted on what the request actually
     * carried rather than on the intent.
     */
    @Test
    fun `only the units it is given are sent`(): Unit = runBlocking {
        val sent = mutableListOf<NarrationChapterRequest>()
        val engine = engine(onSubmit = { sent += it })

        engine.renderChapter(
            request(
                SpokenUnit(0, 20, "The first sentence."),
                SpokenUnit(60, 80, "The third sentence."),
            ),
        )

        assertEquals(1, sent.size)
        assertEquals(2, sent.single().units.size)
        // The offsets are carried through untouched, which is what lets the reader map audio back
        // to the words on screen even though the middle sentence was removed.
        assertEquals(0, sent.single().units[0].startCharacter)
        assertEquals(60, sent.single().units[1].startCharacter)
        assertFalse(
            "text between the units leaked into the request",
            sent.single().units.any { it.text.contains("second") },
        )
    }

    /**
     * A chapter with nothing left to say sends nothing and bills nothing.
     *
     * It still counts as rendered: there is no audio to make, and reporting a failure would put an
     * error in front of a listener for a chapter that was handled correctly.
     */
    @Test
    fun `a fully filtered chapter is not submitted at all`(): Unit = runBlocking {
        var submitted = 0
        val engine = engine(onSubmit = { submitted += 1 })

        val outcome = engine.renderChapter(request())

        assertTrue(outcome is ChapterRenderOutcome.Rendered)
        assertEquals(0L, (outcome as ChapterRenderOutcome.Rendered).durationMs)
        assertEquals("an empty chapter was sent to be synthesized", 0, submitted)
    }

    /**
     * An over-long chapter is refused locally rather than sent and rejected, so the listener is not
     * billed for a request that cannot succeed.
     */
    @Test
    fun `an over-long chapter is refused before it is sent`(): Unit = runBlocking {
        var submitted = 0
        val engine = engine(onSubmit = { submitted += 1 })

        val outcome = engine.renderChapter(
            request(
                SpokenUnit(
                    0,
                    PremiumVoiceEngine.MAXIMUM_CHAPTER_CHARACTERS + 1,
                    "x".repeat(PremiumVoiceEngine.MAXIMUM_CHAPTER_CHARACTERS + 1),
                ),
            ),
        )

        assertEquals(0, submitted)
        val failed = outcome as ChapterRenderOutcome.Failed
        assertFalse("a chapter's length will not change on a retry", failed.retryable)
    }

    // endregion

    // region what comes back

    @Test
    fun `a completed job is written as this chapter's audio`(): Unit = runBlocking {
        val audio = byteArrayOf(1, 2, 3, 4, 5)
        val savedTimelines = mutableMapOf<Int, List<ReaderTimingRange>>()
        val destination = File(temporaryFolder.root, "chapter_0.m4a")
        val engine = engine(
            status = completed(audio, timings = listOf(NarrationUnitTiming(0, 20, 0.0, 1.5))),
            onTimeline = { index, timings -> savedTimelines[index] = timings },
        )

        val outcome = engine.renderChapter(
            request(SpokenUnit(0, 20, "The first sentence."), destination = destination),
        )

        val rendered = outcome as ChapterRenderOutcome.Rendered
        assertEquals(1_500L, rendered.durationMs)
        assertTrue("the audio was not written", destination.isFile)
        assertTrue(audio.contentEquals(destination.readBytes()))
        // A timeline is what the reader highlights from, so a chapter without one is unusable.
        assertEquals(1, savedTimelines[0]?.size)
        assertEquals(0.0, savedTimelines[0]!!.first().startTime, 0.001)
        assertEquals(20, savedTimelines[0]!!.first().endCharacter)
    }

    /** No `.partial` file may survive: a reader that found one would treat it as the chapter. */
    @Test
    fun `no partial file is left behind`(): Unit = runBlocking {
        val destination = File(temporaryFolder.root, "chapter_0.m4a")
        engine(status = completed(byteArrayOf(9, 9, 9))).renderChapter(
            request(SpokenUnit(0, 10, "A sentence."), destination = destination),
        )
        assertTrue(destination.isFile)
        assertFalse(
            "a partial download survived",
            File(destination.absolutePath + ".partial").exists(),
        )
    }

    @Test
    fun `a failed job is reported with the server's reason`(): Unit = runBlocking {
        val engine = engine(
            status = NarrationChapterStatus(
                jobID = "job", chapterIndex = 0, status = "failed",
                error = "The voice is unavailable in your region.",
            ),
        )

        val outcome = engine.renderChapter(request(SpokenUnit(0, 10, "A sentence.")))

        val failed = outcome as ChapterRenderOutcome.Failed
        assertEquals("The voice is unavailable in your region.", failed.reason)
    }

    @Test
    fun `a completed job with no audio is a failure rather than a silent chapter`(): Unit =
        runBlocking {
            val engine = engine(status = completed(ByteArray(0)))
            val outcome = engine.renderChapter(request(SpokenUnit(0, 10, "A sentence.")))
            assertTrue(outcome is ChapterRenderOutcome.Failed)
        }

    // endregion

    // region polling

    /**
     * Polling backs off. A chapter takes tens of seconds, and polling every half second for all of
     * it would be a hundred requests to learn one fact.
     */
    @Test
    fun `polling backs off rather than hammering`(): Unit = runBlocking {
        val waits = mutableListOf<Long>()
        var polls = 0
        val engine = PremiumVoiceEngine(
            voiceID = "Ruth",
            fingerprint = FINGERPRINT,
            submit = { "job" },
            poll = {
                polls += 1
                if (polls < 5) running() else completed(byteArrayOf(1))
            },
            saveTimeline = { _, _ -> },
            pause = { waits += it },
        )

        engine.renderChapter(request(SpokenUnit(0, 10, "A sentence.")))

        assertEquals(listOf(500L, 1_000L, 2_000L, 4_000L), waits)
    }

    @Test
    fun `the poll interval is capped`(): Unit = runBlocking {
        val waits = mutableListOf<Long>()
        var polls = 0
        val engine = PremiumVoiceEngine(
            voiceID = "Ruth",
            fingerprint = FINGERPRINT,
            submit = { "job" },
            poll = { polls += 1; if (polls < 9) running() else completed(byteArrayOf(1)) },
            saveTimeline = { _, _ -> },
            pause = { waits += it },
        )
        engine.renderChapter(request(SpokenUnit(0, 10, "A sentence.")))
        assertTrue(
            "the interval grew past its cap: $waits",
            waits.all { it <= PremiumVoiceEngine.MAXIMUM_POLL_MS },
        )
    }

    /**
     * A dropped poll is not a failed chapter. The work continues on the server, so the next poll
     * finds it -- only the overall deadline ends this.
     */
    @Test
    fun `a dropped poll is retried rather than failing the chapter`(): Unit = runBlocking {
        var polls = 0
        val engine = PremiumVoiceEngine(
            voiceID = "Ruth",
            fingerprint = FINGERPRINT,
            submit = { "job" },
            poll = {
                polls += 1
                if (polls <= 2) throw java.io.IOException("connection dropped")
                completed(byteArrayOf(1))
            },
            saveTimeline = { _, _ -> },
            pause = {},
        )

        val outcome = engine.renderChapter(request(SpokenUnit(0, 10, "A sentence.")))

        assertTrue("a dropped poll failed the chapter", outcome is ChapterRenderOutcome.Rendered)
        assertTrue(polls >= 3)
    }

    /**
     * A job that never resolves must end, and end as retryable.
     *
     * A render that waits for ever is the worst failure available: there is nothing to report and
     * waiting looks like the right thing to do.
     */
    @Test
    fun `a job that never finishes fails rather than waiting for ever`(): Unit = runBlocking {
        val engine = PremiumVoiceEngine(
            voiceID = "Ruth",
            fingerprint = FINGERPRINT,
            submit = { "job" },
            poll = { running() },
            saveTimeline = { _, _ -> },
            pause = {},
        )

        val outcome = engine.renderChapter(request(SpokenUnit(0, 10, "A sentence.")))

        val failed = outcome as ChapterRenderOutcome.Failed
        assertTrue("a timed-out chapter is worth another attempt", failed.retryable)
    }

    // endregion

    // region the contract it shares with the on-device voice

    /**
     * The render coordinator, the queue and the reader must not be able to tell which voice made a
     * chapter. That is what lets one book legitimately hold both -- which is what a lapsed
     * subscription produces.
     */
    @Test
    fun `it is a VoiceEngine like any other`() {
        val engine = engine()
        assertTrue(engine is VoiceEngine)
        assertEquals(VoiceKind.PREMIUM, engine.kind)
        assertTrue(engine.maximumInputCharacters > 0)
    }

    /** Only this voice sends anything anywhere, and it must say so consistently. */
    @Test
    fun `the premium voice is the only one that sends text off the device`() {
        assertTrue(
            com.audiochoice.mobile.narration.NarrationTiers.sendsTextOffDevice(VoiceKind.PREMIUM),
        )
        assertEquals(VoiceKind.PREMIUM, engine().kind)
    }

    // endregion

    // region fixtures

    private fun engine(
        status: NarrationChapterStatus = completed(byteArrayOf(1, 2, 3)),
        onSubmit: (NarrationChapterRequest) -> Unit = {},
        onTimeline: (Int, List<ReaderTimingRange>) -> Unit = { _, _ -> },
    ) = PremiumVoiceEngine(
        voiceID = "Ruth",
        fingerprint = FINGERPRINT,
        submit = { request -> onSubmit(request); "job-1" },
        poll = { status },
        saveTimeline = { index, timings -> onTimeline(index, timings) },
        pause = {},
    )

    private fun request(
        vararg units: SpokenUnit,
        destination: File = File(temporaryFolder.root, "chapter.m4a"),
    ) = ChapterRenderRequest(
        bookKey = FINGERPRINT.sha256,
        chapterIndex = 0,
        language = "en",
        units = units.toList(),
        destination = destination,
    )

    private fun running() = NarrationChapterStatus(
        jobID = "job-1", chapterIndex = 0, status = "running",
    )

    private fun completed(
        audio: ByteArray,
        timings: List<NarrationUnitTiming> = emptyList(),
    ) = NarrationChapterStatus(
        jobID = "job-1",
        chapterIndex = 0,
        status = "completed",
        provider = "polly",
        modelVersion = "polly-generative",
        voiceID = "Ruth",
        durationSeconds = 1.5,
        timings = timings,
        audioBase64 = Base64.getEncoder().encodeToString(audio),
    )

    private companion object {
        val FINGERPRINT = BookFingerprint(
            version = 1,
            sha256 = "a".repeat(64),
            fileSize = 1_024,
            duration = null,
            fileType = "epub",
        )
    }

    // endregion
}
