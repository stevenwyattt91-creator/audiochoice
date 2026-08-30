package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.NarrationChapter
import com.audiochoice.mobile.data.NarrationPlan
import com.audiochoice.mobile.data.NarrationUnit
import com.audiochoice.mobile.data.PlanInputs
import com.audiochoice.mobile.data.ReaderTimingRange
import com.audiochoice.mobile.data.RenderQueue
import com.audiochoice.mobile.data.RenderState
import com.audiochoice.mobile.data.VoiceKind
import com.audiochoice.mobile.narration.voice.ChapterRenderOutcome
import com.audiochoice.mobile.narration.voice.ChapterRenderRequest
import com.audiochoice.mobile.narration.voice.VoiceEngine
import com.audiochoice.mobile.reader.ReaderMask
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

/**
 * The render loop's behaviour, exercised with a fake voice and a real store.
 *
 * The `WorkManager` shell around this is deliberately thin, so what is worth testing
 * is here: which chapters get produced, what is persisted after each one, what happens
 * to a chapter that fails or is interrupted, and what the listener is shown while it
 * runs. None of that needs a device.
 */
class NarrationRenderCoordinatorTest {

    @get:Rule
    val temporaryFolder = TemporaryFolder()

    private val sha = "d".repeat(64)

    // region the loop

    /**
     * The window bounds the pass. Producing chapters a listener never reaches is what
     * the window exists to prevent, on a phone's battery and on a premium bill alike.
     */
    @Test
    fun `rendering stops once the window is satisfied`() = runBlocking {
        val engine = FakeVoiceEngine()
        val plan = planOf(chapters = 6)
        val pass = coordinator(engine, window = RenderAheadWindow(2)).renderPending(
            sha256 = sha,
            plan = plan,
            filteredRanges = emptyList(),
            playheadChapter = 0,
        )

        // The playhead's own chapter plus two ahead of it.
        assertEquals(listOf(0, 1, 2), engine.renderedChapters)
        assertEquals(StopReason.WINDOW_SATISFIED, pass.stopReason)
        assertEquals(3, pass.queue.renderedCount)
    }

    @Test
    fun `a full book request renders every chapter regardless of the window`() = runBlocking {
        val engine = FakeVoiceEngine()
        val pass = coordinator(engine, window = RenderAheadWindow(1)).renderPending(
            sha256 = sha,
            plan = planOf(chapters = 4),
            filteredRanges = emptyList(),
            fullBookRequested = true,
        )

        assertEquals(listOf(0, 1, 2, 3), engine.renderedChapters)
        assertEquals(StopReason.NOTHING_LEFT, pass.stopReason)
        assertTrue(pass.queue.isFullyRendered)
    }

    @Test
    fun `a pause request renders nothing and keeps every state`() = runBlocking {
        val engine = FakeVoiceEngine()
        val pass = coordinator(engine).renderPending(
            sha256 = sha,
            plan = planOf(chapters = 3),
            filteredRanges = emptyList(),
            pausedByListener = true,
        )

        assertTrue(engine.renderedChapters.isEmpty())
        assertEquals(StopReason.PAUSED, pass.stopReason)
        assertTrue(pass.queue.states.all { it == RenderState.NOT_RENDERED })
    }

    /** Everything a chapter produced is persisted before the next one starts. */
    @Test
    fun `each rendered chapter persists its duration, timeline and counts`() = runBlocking {
        val engine = FakeVoiceEngine(durationMs = 12_000L)
        val store = store()
        coordinator(engine, store = store, window = RenderAheadWindow(1)).renderPending(
            sha256 = sha,
            plan = planOf(chapters = 3),
            filteredRanges = emptyList(),
        )

        val queue = store.loadQueue(sha)!!
        assertEquals(12_000L, queue.chapterDurationsMs[0])
        assertEquals(RenderState.RENDERED, queue.states[0])
        assertTrue(store.loadChapterTimeline(sha, 0)!!.isNotEmpty())
    }

    // endregion

    // region filtering

    /**
     * A chapter whose every unit is filtered out counts as rendered with no audio. It
     * has nothing left to produce, so leaving it in the queue would stall the book.
     */
    @Test
    fun `a fully filtered chapter renders as silence without calling the engine`() = runBlocking {
        val engine = FakeVoiceEngine()
        val plan = planOf(chapters = 2)
        val store = store()

        // Cover the whole of chapter 0.
        val chapterZero = plan.chapters[0]
        val pass = coordinator(engine, store = store, window = RenderAheadWindow(1)).renderPending(
            sha256 = sha,
            plan = plan,
            filteredRanges = listOf(ReaderMask(chapterZero.startCharacter, chapterZero.endCharacter)),
        )

        assertFalse(engine.renderedChapters.contains(0))
        assertEquals(RenderState.RENDERED, pass.queue.states[0])
        assertEquals(0L, pass.queue.chapterDurationsMs[0])
        assertTrue(store.loadChapterTimeline(sha, 0)!!.isEmpty())
    }

    /** Not one filtered character is ever handed to a voice. */
    @Test
    fun `filtered characters never reach the engine`() = runBlocking {
        val engine = FakeVoiceEngine()
        val plan = planOf(chapters = 1)
        val unit = plan.chapters[0].units.first()
        val secret = "SECRET"
        val planWithSecret = plan.copy(
            chapters = listOf(
                plan.chapters[0].copy(
                    units = listOf(
                        NarrationUnit(
                            unit.startCharacter,
                            unit.startCharacter + secret.length,
                            secret,
                        ),
                        unit.copy(
                            startCharacter = unit.startCharacter + secret.length + 1,
                            endCharacter = unit.startCharacter + secret.length + 1 + unit.length,
                        ),
                    ),
                ),
            ),
        )

        coordinator(engine).renderPending(
            sha256 = sha,
            plan = planWithSecret,
            filteredRanges = listOf(
                ReaderMask(unit.startCharacter, unit.startCharacter + secret.length),
            ),
        )

        assertTrue(engine.submittedText.none { it.contains(secret) })
    }

    /** Omission counts are recorded per chapter, for the coverage report. */
    @Test
    fun `omission counts are persisted per chapter`() = runBlocking {
        val engine = FakeVoiceEngine()
        val plan = planOf(chapters = 1, unitsPerChapter = 3)
        val store = store()
        val firstUnit = plan.chapters[0].units.first()

        coordinator(engine, store = store).renderPending(
            sha256 = sha,
            plan = plan,
            filteredRanges = listOf(ReaderMask(firstUnit.startCharacter, firstUnit.endCharacter)),
        )

        assertEquals(1, store.loadQueue(sha)!!.omittedUnitCounts[0])
    }

    // endregion

    // region pronunciation

    /**
     * Rules change what is spoken, never what the offsets mean, so the range recorded
     * for a unit is untouched by them.
     */
    @Test
    fun `pronunciation rules change the text sent but not the recorded range`() = runBlocking {
        val engine = FakeVoiceEngine()
        val plan = planOf(chapters = 1)
        val store = store()

        coordinator(
            engine,
            store = store,
            pronounce = { it.replace("lantern", "LAN-tern") },
        ).renderPending(sha256 = sha, plan = plan, filteredRanges = emptyList())

        assertTrue(engine.submittedText.any { it.contains("LAN-tern") })
        val timing = store.loadChapterTimeline(sha, 0)!!.single()
        assertEquals(plan.chapters[0].units.single().startCharacter, timing.startCharacter)
        assertEquals(plan.chapters[0].units.single().endCharacter, timing.endCharacter)
    }

    // endregion

    // region failure

    /**
     * A failed chapter does not stall the book: the loop steps past it and keeps going,
     * and it is not retried automatically.
     */
    @Test
    fun `a failed chapter is recorded and the loop continues past it`() = runBlocking {
        val engine = FakeVoiceEngine(failChapters = setOf(1))
        val pass = coordinator(engine, window = RenderAheadWindow(5)).renderPending(
            sha256 = sha,
            plan = planOf(chapters = 4),
            filteredRanges = emptyList(),
        )

        assertEquals(RenderState.RENDER_FAILED, pass.queue.states[1])
        assertEquals(RenderState.RENDERED, pass.queue.states[2])
        assertEquals(RenderState.RENDERED, pass.queue.states[3])
        assertTrue(pass.queue.failureReasons.containsKey(1))
        // Attempted once, not three times: the retry budget lives inside the engine.
        assertEquals(1, engine.attempts[1])
    }

    /** Rendered audio stays playable when a later chapter fails. */
    @Test
    fun `a failure leaves earlier rendered chapters intact`() = runBlocking {
        val engine = FakeVoiceEngine(failChapters = setOf(1), durationMs = 5_000L)
        val pass = coordinator(engine, window = RenderAheadWindow(5)).renderPending(
            sha256 = sha,
            plan = planOf(chapters = 3),
            filteredRanges = emptyList(),
        )

        assertEquals(RenderState.RENDERED, pass.queue.states[0])
        assertEquals(5_000L, pass.queue.chapterDurationsMs[0])
    }

    @Test
    fun `a book whose every chapter failed reports the all failed state`() = runBlocking {
        val engine = FakeVoiceEngine(failChapters = setOf(0, 1))
        val pass = coordinator(engine, window = RenderAheadWindow(5)).renderPending(
            sha256 = sha,
            plan = planOf(chapters = 2),
            filteredRanges = emptyList(),
        )

        assertEquals(StopReason.ALL_FAILED, pass.stopReason)
    }

    /** A retry returns one chapter to the queue without disturbing anything else. */
    @Test
    fun `retrying a chapter returns only that chapter to the queue`() = runBlocking {
        val engine = FakeVoiceEngine(failChapters = setOf(1), durationMs = 4_000L)
        val store = store()
        val coordinator = coordinator(engine, store = store, window = RenderAheadWindow(5))
        coordinator.renderPending(sha, planOf(chapters = 3), emptyList())

        val queue = coordinator.retryChapter(sha, 1)!!

        assertEquals(RenderState.NOT_RENDERED, queue.states[1])
        assertEquals(RenderState.RENDERED, queue.states[0])
        assertEquals(4_000L, queue.chapterDurationsMs[0])
        assertFalse(queue.failureReasons.containsKey(1))
    }

    @Test
    fun `retrying a chapter that did not fail changes nothing`() = runBlocking {
        val engine = FakeVoiceEngine(durationMs = 4_000L)
        val store = store()
        val coordinator = coordinator(engine, store = store, window = RenderAheadWindow(1))
        coordinator.renderPending(sha, planOf(chapters = 2), emptyList())

        val queue = coordinator.retryChapter(sha, 0)!!

        assertEquals(RenderState.RENDERED, queue.states[0])
    }

    @Test
    fun `retrying all failed chapters clears every reason`() = runBlocking {
        val engine = FakeVoiceEngine(failChapters = setOf(0, 1))
        val store = store()
        val coordinator = coordinator(engine, store = store, window = RenderAheadWindow(5))
        coordinator.renderPending(sha, planOf(chapters = 2), emptyList())

        val queue = coordinator.retryAllFailedChapters(sha)!!

        assertTrue(queue.states.all { it == RenderState.NOT_RENDERED })
        assertTrue(queue.failureReasons.isEmpty())
    }

    // endregion

    // region interruption

    /**
     * Cancellation returns the chapter to the queue rather than marking it failed.
     * Nothing was wrong with it, and recording a failure would eventually make a
     * perfectly renderable chapter unrenderable.
     */
    @Test
    fun `cancellation returns the chapter to the queue and stops the pass`() = runBlocking {
        val engine = FakeVoiceEngine(cancelChapters = setOf(1), durationMs = 3_000L)
        val pass = coordinator(engine, window = RenderAheadWindow(5)).renderPending(
            sha256 = sha,
            plan = planOf(chapters = 3),
            filteredRanges = emptyList(),
        )

        assertEquals(StopReason.CANCELLED, pass.stopReason)
        assertEquals(RenderState.RENDERED, pass.queue.states[0])
        assertEquals(RenderState.NOT_RENDERED, pass.queue.states[1])
        // Stopped, rather than skipping ahead.
        assertFalse(engine.renderedChapters.contains(2))
    }

    /**
     * A partial file is what process death leaves behind, and a clean cancellation path
     * cannot cover it because process death does not run one. Sweeping at the start of
     * every pass is what makes the guarantee hold across a killed process.
     */
    @Test
    fun `a partial file left by a killed process is deleted and its chapter reset`() = runBlocking {
        val store = store()
        val plan = planOf(chapters = 3)
        // Simulate a process that died mid-chapter: chapter 1 marked RENDERING, with a
        // partial file on disk.
        store.saveQueue(
            sha,
            RenderQueue.forPlan(plan).withState(1, RenderState.RENDERING),
        )
        val partial = store.partialChapterAudioFile(sha, 1)
        partial.parentFile?.mkdirs()
        partial.writeBytes(ByteArray(128))

        val engine = FakeVoiceEngine()
        coordinator(engine, store = store, window = RenderAheadWindow(1)).renderPending(
            sha256 = sha,
            plan = plan,
            filteredRanges = emptyList(),
        )

        assertFalse("partial audio must not survive", partial.exists())
        // Reset, then rendered again from its first unit.
        assertTrue(engine.renderedChapters.contains(1))
    }

    /** A chapter stuck in RENDERING is interrupted even without a partial file. */
    @Test
    fun `a chapter left mid render is returned to the queue`() = runBlocking {
        val store = store()
        val plan = planOf(chapters = 2)
        store.saveQueue(sha, RenderQueue.forPlan(plan).withState(0, RenderState.RENDERING))

        val engine = FakeVoiceEngine()
        val pass = coordinator(engine, store = store, window = RenderAheadWindow(1)).renderPending(
            sha256 = sha,
            plan = plan,
            filteredRanges = emptyList(),
        )

        assertEquals(RenderState.RENDERED, pass.queue.states[0])
        assertTrue(engine.renderedChapters.contains(0))
    }

    /**
     * A plan rebuilt with a different chapter count would leave a persisted queue
     * indexing chapters that no longer exist.
     */
    @Test
    fun `a queue from a differently shaped plan is rebuilt`() = runBlocking {
        val store = store()
        store.saveQueue(sha, RenderQueue.forPlan(planOf(chapters = 9)))

        val engine = FakeVoiceEngine()
        val pass = coordinator(engine, store = store, window = RenderAheadWindow(1)).renderPending(
            sha256 = sha,
            plan = planOf(chapters = 2),
            filteredRanges = emptyList(),
        )

        assertEquals(2, pass.queue.states.size)
    }

    // endregion

    // region progress

    @Test
    fun `progress reports counts and the chapter being rendered`() = runBlocking {
        val engine = FakeVoiceEngine(durationMs = 1_000L)
        val updates = mutableListOf<NarrationProgress>()
        coordinator(engine, window = RenderAheadWindow(1), onProgress = updates::add)
            .renderPending(sha, planOf(chapters = 3), emptyList())

        assertTrue(updates.isNotEmpty())
        assertTrue(updates.any { it.renderingChapterTitle == "Chapter 1" })
        assertEquals(3, updates.last().totalChapters)
        assertNull("no chapter is rendering once the pass ends", updates.last().renderingChapterTitle)
        // Duration grows as chapters land, which is what the player reports as the
        // book's length.
        assertTrue(updates.last().renderedDurationMs > 0)
    }

    @Test
    fun `progress reports failures separately from completions`() = runBlocking {
        val engine = FakeVoiceEngine(failChapters = setOf(0))
        val updates = mutableListOf<NarrationProgress>()
        coordinator(engine, window = RenderAheadWindow(5), onProgress = updates::add)
            .renderPending(sha, planOf(chapters = 2), emptyList())

        assertEquals(1, updates.last().failedChapters)
        assertEquals(1, updates.last().renderedChapters)
        assertEquals(0, updates.last().remainingChapters)
        assertFalse(updates.last().isComplete)
    }

    // endregion

    // region fixtures

    private fun store() = NarrationStore(temporaryFolder.root)

    private fun coordinator(
        engine: VoiceEngine,
        store: NarrationStore = store(),
        window: RenderAheadWindow = RenderAheadWindow.DEFAULT,
        pronounce: (String) -> String = { it },
        onProgress: (NarrationProgress) -> Unit = {},
    ) = NarrationRenderCoordinator(store, engine, window, pronounce, onProgress)

    private fun planOf(chapters: Int, unitsPerChapter: Int = 1): NarrationPlan {
        var offset = 0
        val text = "The lantern swung against the rigging all night long."
        return NarrationPlan(
            planVersion = NarrationPlan.PLAN_VERSION,
            inputs = PlanInputs(
                sourceSha256 = sha,
                bookTextHash = "hash",
                extractionVersion = 1,
                planVersion = NarrationPlan.PLAN_VERSION,
                synthesisInputLimit = 1_000,
            ),
            chapters = (0 until chapters).map { index ->
                val start = offset
                val units = (0 until unitsPerChapter).map {
                    val unit = NarrationUnit(offset, offset + text.length, text)
                    offset += text.length + 1
                    unit
                }
                NarrationChapter(index, "Chapter ${index + 1}", start, offset, units)
            },
        )
    }

    /**
     * Stands in for a voice. Records which chapters it was asked for and what text it
     * was given, which is how the filtering assertions are made.
     */
    private class FakeVoiceEngine(
        private val durationMs: Long = 1_000L,
        private val failChapters: Set<Int> = emptySet(),
        private val cancelChapters: Set<Int> = emptySet(),
    ) : VoiceEngine {
        override val kind = VoiceKind.SYSTEM
        override val voiceID = "fake"
        override val maximumInputCharacters = 1_000

        val renderedChapters = mutableListOf<Int>()
        val submittedText = mutableListOf<String>()
        val attempts = mutableMapOf<Int, Int>()

        override suspend fun renderChapter(request: ChapterRenderRequest): ChapterRenderOutcome {
            attempts[request.chapterIndex] = (attempts[request.chapterIndex] ?: 0) + 1
            request.units.forEach { submittedText += it.text }

            if (request.chapterIndex in cancelChapters) return ChapterRenderOutcome.Cancelled
            if (request.chapterIndex in failChapters) {
                return ChapterRenderOutcome.Failed("fake failure", retryable = false)
            }

            renderedChapters += request.chapterIndex
            request.destination.parentFile?.mkdirs()
            request.destination.writeBytes(ByteArray(32))

            var elapsed = 0.0
            val perUnit = durationMs / request.units.size.coerceAtLeast(1) / 1_000.0
            val timings = request.units.map { unit ->
                val range = ReaderTimingRange(
                    startTime = elapsed,
                    endTime = elapsed + perUnit,
                    startCharacter = unit.startCharacter,
                    endCharacter = unit.endCharacter,
                )
                elapsed += perUnit
                range
            }
            return ChapterRenderOutcome.Rendered(request.destination, durationMs, timings)
        }
    }

    // endregion
}
