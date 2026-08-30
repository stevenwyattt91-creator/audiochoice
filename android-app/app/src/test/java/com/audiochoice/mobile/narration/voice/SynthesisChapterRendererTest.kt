package com.audiochoice.mobile.narration.voice

import com.audiochoice.mobile.narration.SpokenUnit
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.async
import kotlinx.coroutines.delay
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

/**
 * The render policy -- splitting, retrying, timing out, building timings -- lives above
 * the platform seams precisely so it can be tested like this: no device, no real
 * voice, no encoder. What the fakes stand in for is a framework call whose behaviour a
 * test could only ever assert against a mock anyway.
 */
class SynthesisChapterRendererTest {

    @get:Rule
    val temporaryFolder = TemporaryFolder()

    // region timings

    /**
     * Exactly one timing per unit, whatever happened underneath. The reader highlights
     * a sentence, so a unit that needed three requests must still be one range.
     */
    @Test
    fun `each unit produces exactly one timing range`() = runTest {
        val synthesizer = FakeSynthesizer(millisecondsPerCharacter = 10)
        val outcome = render(synthesizer, units = listOf(unit(0, "One."), unit(10, "Two.")))

        val rendered = outcome as ChapterRenderOutcome.Rendered
        assertEquals(2, rendered.timings.size)
        assertEquals(0, rendered.timings[0].startCharacter)
        assertEquals(10, rendered.timings[1].startCharacter)
    }

    /** Timings are chapter-relative and contiguous, which is what the timeline expects. */
    @Test
    fun `timings are chapter relative and run consecutively`() = runTest {
        val synthesizer = FakeSynthesizer(millisecondsPerCharacter = 10)
        val outcome = render(
            synthesizer,
            units = listOf(unit(0, "aaaa"), unit(10, "bbbbbbbb"), unit(30, "cc")),
        )

        val rendered = outcome as ChapterRenderOutcome.Rendered
        assertEquals(0.0, rendered.timings[0].startTime, 0.001)
        assertEquals(rendered.timings[0].endTime, rendered.timings[1].startTime, 0.001)
        assertEquals(rendered.timings[1].endTime, rendered.timings[2].startTime, 0.001)
        assertEquals(rendered.durationMs / 1000.0, rendered.timings.last().endTime, 0.001)
    }

    /**
     * A unit too long for the engine is sent as several requests and still records one
     * range, spanning the whole unit.
     */
    @Test
    fun `a split unit still records one range spanning the whole unit`() = runTest {
        val synthesizer = FakeSynthesizer(millisecondsPerCharacter = 10, maximumInput = 12)
        val text = "one two three four five six seven"
        val outcome = render(synthesizer, units = listOf(unit(100, text)))

        val rendered = outcome as ChapterRenderOutcome.Rendered
        assertTrue("expected several requests", synthesizer.requests.size > 1)
        assertEquals(1, rendered.timings.size)
        assertEquals(100, rendered.timings.single().startCharacter)
        assertEquals(100 + text.length, rendered.timings.single().endCharacter)
    }

    /** A chapter whose every unit was filtered out renders as silence, not as a failure. */
    @Test
    fun `an empty chapter renders as zero duration with no file`() = runTest {
        val synthesizer = FakeSynthesizer()
        val outcome = render(synthesizer, units = emptyList())

        val rendered = outcome as ChapterRenderOutcome.Rendered
        assertEquals(0L, rendered.durationMs)
        assertTrue(rendered.timings.isEmpty())
        assertTrue(synthesizer.requests.isEmpty())
    }

    // endregion

    // region retries and timeouts

    /** Three attempts in total: the first plus two retries. */
    @Test
    fun `a failing request is attempted three times before the chapter fails`() = runTest {
        val synthesizer = FakeSynthesizer(failEveryRequest = true)
        val outcome = render(synthesizer, units = listOf(unit(0, "One.")))

        assertTrue(outcome is ChapterRenderOutcome.Failed)
        assertFalse((outcome as ChapterRenderOutcome.Failed).retryable)
        assertEquals(3, synthesizer.requests.size)
    }

    @Test
    fun `a request that succeeds on its second attempt renders the chapter`() = runTest {
        val synthesizer = FakeSynthesizer(failFirstRequests = 1)
        val outcome = render(synthesizer, units = listOf(unit(0, "One.")))

        assertTrue(outcome is ChapterRenderOutcome.Rendered)
        assertEquals(2, synthesizer.requests.size)
    }

    /**
     * A hung engine is the same thing as a failed one from the listener's side: a
     * chapter that is not arriving. Without the timeout it would hold the queue with
     * nothing to report.
     */
    @Test
    fun `a request that never returns times out and is retried`() = runTest {
        val synthesizer = FakeSynthesizer(hangForever = true)
        val outcome = render(synthesizer, units = listOf(unit(0, "One.")), timeoutMs = 1_000)

        assertTrue(outcome is ChapterRenderOutcome.Failed)
        assertTrue((outcome as ChapterRenderOutcome.Failed).reason.contains("timed out"))
        assertEquals(3, synthesizer.requests.size)
    }

    /**
     * An engine that claims success but writes nothing is treated as a failure. Trusting
     * it would produce a timeline entry describing audio that does not exist.
     */
    @Test
    fun `an engine reporting success without writing audio is a failure`() = runTest {
        val synthesizer = FakeSynthesizer(reportSuccessWithoutWriting = true)
        val outcome = render(synthesizer, units = listOf(unit(0, "One.")))

        assertTrue(outcome is ChapterRenderOutcome.Failed)
        assertTrue((outcome as ChapterRenderOutcome.Failed).reason.contains("no audio"))
    }

    /**
     * Cancellation is not failure. Treating it as one would consume the retry budget and
     * eventually mark a chapter unrenderable for something that was never wrong with it.
     */
    @Test
    fun `cancellation is reported as cancelled and consumes no retries`() = runTest {
        val gate = CompletableDeferred<Unit>()
        val synthesizer = FakeSynthesizer(cancelOnRequest = gate)
        val outcome = render(synthesizer, units = listOf(unit(0, "One.")))

        assertEquals(ChapterRenderOutcome.Cancelled, outcome)
        assertEquals(1, synthesizer.requests.size)
    }

    // endregion

    // region no partial output survives

    /**
     * A finished chapter is renamed into place, so a reader never observes a
     * half-written file. A failed one leaves nothing behind at all.
     */
    @Test
    fun `a failed render leaves no partial file behind`() = runTest {
        val destination = File(temporaryFolder.root, "audio/chapter_0.m4a")
        val synthesizer = FakeSynthesizer(failEveryRequest = true)

        render(synthesizer, units = listOf(unit(0, "One.")), destination = destination)

        assertFalse(destination.exists())
        val leftovers = destination.parentFile?.listFiles().orEmpty()
            .filter { it.name.contains(".partial") }
        assertTrue(leftovers.toString(), leftovers.isEmpty())
    }

    @Test
    fun `a successful render leaves the finished file and no scratch`() = runTest {
        val destination = File(temporaryFolder.root, "audio/chapter_0.m4a")
        val synthesizer = FakeSynthesizer(millisecondsPerCharacter = 10)

        val outcome = render(synthesizer, units = listOf(unit(0, "One.")), destination = destination)

        assertTrue(outcome is ChapterRenderOutcome.Rendered)
        assertTrue(destination.isFile)
        assertTrue(
            destination.parentFile!!.listFiles().orEmpty().none { it.name.endsWith(".partial") },
        )
    }

    // endregion

    // region splitting

    /**
     * Checked after pronunciation rules rather than at plan time, because replacing a
     * name with a phonetic spelling routinely makes text longer, so a unit that fitted
     * when planned may not fit when spoken.
     */
    @Test
    fun `text is split at word boundaries within the ceiling`() {
        val pieces = SynthesisChapterRenderer.splitToCeiling("one two three four five", 10)

        assertTrue(pieces.all { it.length <= 10 })
        pieces.forEach { piece ->
            assertEquals(piece.trim(), piece)
        }
        assertEquals("one two three four five", pieces.joinToString(" "))
    }

    @Test
    fun `text within the ceiling is not split`() {
        assertEquals(listOf("short enough"), SynthesisChapterRenderer.splitToCeiling("short enough", 50))
    }

    /** A token longer than the ceiling is cut rather than sent and rejected. */
    @Test
    fun `an unbreakable token is cut to fit`() {
        val pieces = SynthesisChapterRenderer.splitToCeiling("a".repeat(25), 10)

        assertTrue(pieces.all { it.length <= 10 })
        assertEquals(25, pieces.sumOf { it.length })
    }

    // endregion

    // region fakes

    private fun unit(start: Int, text: String) = SpokenUnit(start, start + text.length, text)

    private suspend fun render(
        synthesizer: FakeSynthesizer,
        units: List<SpokenUnit>,
        destination: File = File(temporaryFolder.root, "audio/chapter_0.m4a"),
        timeoutMs: Long = 30_000,
    ): ChapterRenderOutcome {
        val scratch = File(temporaryFolder.root, "scratch").apply { mkdirs() }
        val renderer = SynthesisChapterRenderer(
            synthesizer = synthesizer,
            writerFactory = { file -> FakeWriter(file, synthesizer) },
            scratchFile = { index -> File(scratch, "utterance_$index.wav") },
            requestTimeoutMs = timeoutMs,
        )
        return renderer.render(
            ChapterRenderRequest(
                bookKey = "a".repeat(64),
                chapterIndex = 0,
                language = "en",
                units = units,
                destination = destination,
            ),
        )
    }

    /**
     * Stands in for a speech engine. Records what it was asked to say, so tests can
     * assert on request counts and on the fact that nothing filtered was ever sent.
     */
    private class FakeSynthesizer(
        override val maximumInputCharacters: Int = 1_000,
        val millisecondsPerCharacter: Int = 10,
        val failEveryRequest: Boolean = false,
        val failFirstRequests: Int = 0,
        val hangForever: Boolean = false,
        val reportSuccessWithoutWriting: Boolean = false,
        val cancelOnRequest: CompletableDeferred<Unit>? = null,
    ) : SpeechSynthesizer {

        constructor(millisecondsPerCharacter: Int, maximumInput: Int) : this(
            maximumInputCharacters = maximumInput,
            millisecondsPerCharacter = millisecondsPerCharacter,
        )

        val requests = mutableListOf<String>()

        /** Duration this fake claims for the last written file, keyed by path. */
        val durations = mutableMapOf<String, Long>()

        override suspend fun synthesize(text: String, destination: File): Boolean {
            requests += text

            cancelOnRequest?.let { throw kotlinx.coroutines.CancellationException("listener paused") }
            if (hangForever) {
                delay(Long.MAX_VALUE)
                return true
            }
            if (failEveryRequest) return false
            if (requests.size <= failFirstRequests) return false
            if (reportSuccessWithoutWriting) return true

            destination.parentFile?.mkdirs()
            destination.writeBytes(ByteArray(16))
            durations[destination.absolutePath] = text.length.toLong() * millisecondsPerCharacter
            return true
        }
    }

    /**
     * Stands in for the encoder. Reports the running duration the same way the real
     * writer does -- from what was appended -- so the timing arithmetic under test is
     * the real arithmetic.
     */
    private class FakeWriter(
        private val destination: File,
        private val synthesizer: FakeSynthesizer,
    ) : ChapterAudioWriter {
        private var total = 0L
        private var finished = false

        init {
            destination.parentFile?.mkdirs()
            destination.writeBytes(ByteArray(0))
        }

        override fun append(source: File): Long {
            total += synthesizer.durations[source.absolutePath] ?: 0L
            destination.appendBytes(ByteArray(8))
            return total
        }

        override fun finish(): Long {
            finished = true
            return total
        }

        override fun close() {
            if (!finished) destination.delete()
        }
    }

    // endregion
}
