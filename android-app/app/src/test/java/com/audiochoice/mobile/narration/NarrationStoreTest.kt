package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.NarrationChapter
import com.audiochoice.mobile.data.NarrationPlan
import com.audiochoice.mobile.data.NarrationUnit
import com.audiochoice.mobile.data.PlanInputs
import com.audiochoice.mobile.data.ReaderTimingRange
import com.audiochoice.mobile.data.RenderQueue
import com.audiochoice.mobile.data.RenderState
import io.kotest.property.Arb
import io.kotest.property.PropTestConfig
import io.kotest.property.arbitrary.int
import io.kotest.property.arbitrary.list
import io.kotest.property.arbitrary.map
import io.kotest.property.arbitrary.numericDouble
import io.kotest.property.arbitrary.string
import io.kotest.property.checkAll
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import kotlin.math.abs

class NarrationStoreTest {

    @get:Rule
    val temporaryFolder = TemporaryFolder()

    private val json = Json { ignoreUnknownKeys = true }

    private fun store() = NarrationStore(temporaryFolder.root)

    private val sha = "a".repeat(64)

    // region round-trip properties

    /**
     * A plan survives serialisation unchanged, including every `PlanInputs`
     * member.
     *
     * `PlanInputs` is the part that actually matters here and the part easiest to
     * lose: it is what detects a stale plan on the next open. If a field were
     * dropped in serialisation the plan would look fresh forever, and the app
     * would render against offsets belonging to text that had changed.
     */
    @Test
    fun `plan round trips structurally including plan inputs`(): Unit = runBlocking {
        checkAll(PropTestConfig(iterations = 120), planArb()) { plan ->
            val restored = json.decodeFromString<NarrationPlan>(json.encodeToString(plan))

            assertEquals(plan.planVersion, restored.planVersion)
            assertEquals(plan.inputs, restored.inputs)
            assertEquals(plan.chapterDerivationFellBackToSpine, restored.chapterDerivationFellBackToSpine)
            assertEquals(plan.chapters.size, restored.chapters.size)
            plan.chapters.zip(restored.chapters).forEach { (original, copy) ->
                assertEquals(original.index, copy.index)
                assertEquals(original.title, copy.title)
                assertEquals(original.startCharacter, copy.startCharacter)
                assertEquals(original.endCharacter, copy.endCharacter)
                assertEquals(original.units, copy.units)
            }
        }
    }

    /**
     * A timeline round trips with offsets exact and times within a millisecond.
     *
     * Offsets have to be exact because they address characters; a drift of one
     * would highlight the wrong word. Times are allowed a millisecond because
     * they are doubles in seconds, and a millisecond is far below anything a
     * listener can hear.
     */
    @Test
    fun `timeline round trips with exact offsets and millisecond time tolerance`(): Unit = runBlocking {
        checkAll(PropTestConfig(iterations = 120), Arb.list(timingArb(), 0..40)) { timings ->
            val restored = json.decodeFromString<List<ReaderTimingRange>>(json.encodeToString(timings))

            assertEquals(timings.size, restored.size)
            timings.zip(restored).forEach { (original, copy) ->
                assertEquals(original.startCharacter, copy.startCharacter)
                assertEquals(original.endCharacter, copy.endCharacter)
                assertTrue(abs(original.startTime - copy.startTime) <= 0.001)
                assertTrue(abs(original.endTime - copy.endTime) <= 0.001)
            }
        }
    }

    // endregion

    // region performance

    /**
     * A full novel's plan has to load fast enough to sit in the open path of a
     * book. Twenty thousand units is a long novel, and the two-second budget is
     * per direction.
     */
    @Test
    fun `twenty thousand unit plan serialises and deserialises within two seconds each`() {
        val plan = largePlan(unitCount = 20_000)

        val encodeStart = System.nanoTime()
        val encoded = json.encodeToString(plan)
        val encodeMs = (System.nanoTime() - encodeStart) / 1_000_000

        val decodeStart = System.nanoTime()
        val decoded = json.decodeFromString<NarrationPlan>(encoded)
        val decodeMs = (System.nanoTime() - decodeStart) / 1_000_000

        assertEquals(20_000, decoded.totalUnits)
        assertTrue("encode took ${encodeMs}ms", encodeMs < 2_000)
        assertTrue("decode took ${decodeMs}ms", decodeMs < 2_000)
    }

    // endregion

    // region persistence and staleness

    @Test
    fun `plan and queue survive a save and load`() = runBlocking {
        val store = store()
        val plan = largePlan(unitCount = 12)
        val hash = plan.inputs.bookTextHash

        store.savePlan(sha, plan)
        store.saveQueue(sha, RenderQueue.forPlan(plan))

        val loaded = store.loadPlan(sha, hash)
        assertTrue(loaded is PlanLoad.Loaded)
        assertEquals(plan, (loaded as PlanLoad.Loaded).plan)
        assertEquals(plan.chapters.size, store.loadQueue(sha)?.chapterCount)
    }

    @Test
    fun `absent plan is reported as absent rather than stale`() = runBlocking {
        assertEquals(PlanLoad.Absent, store().loadPlan(sha, "any"))
    }

    /**
     * A book-text change invalidates the scan results as well as the plan,
     * because the events carry character offsets into text that has moved. A
     * plan-version change does not: the offsets still mean what they meant, so
     * re-scanning would cost the listener a round trip for nothing.
     */
    @Test
    fun `book text hash change discards the scan results but a plan version change keeps them`() =
        runBlocking {
            val store = store()
            val plan = largePlan(unitCount = 4)
            store.savePlan(sha, plan)
            store.saveBookText(sha, "book text")
            val scanFile = java.io.File(store.bookDirectory(sha), NarrationStore.TEXT_SCAN_FILE)
            scanFile.parentFile?.mkdirs()
            scanFile.writeText("[]")

            val stale = store.loadPlan(sha, currentBookTextHash = "a-different-hash")
            assertEquals(PlanLoad.Stale(StaleReason.BOOK_TEXT_HASH), stale)

            store.discardStalePlan(sha, StaleReason.BOOK_TEXT_HASH)
            assertNull(store.bookText(sha))
            assertTrue(!scanFile.exists())

            // Same book, plan-version staleness this time: the scan survives.
            store.savePlan(sha, plan)
            scanFile.writeText("[]")
            store.discardStalePlan(sha, StaleReason.PLAN_VERSION)
            assertTrue(scanFile.exists())
        }

    @Test
    fun `unreadable plan is reported as unreadable and leaves the book directory intact`() =
        runBlocking {
            val store = store()
            val planFile = java.io.File(store.bookDirectory(sha), NarrationStore.PLAN_FILE)
            planFile.parentFile?.mkdirs()
            planFile.writeText("{ this is not json")

            assertEquals(PlanLoad.Stale(StaleReason.UNREADABLE), store.loadPlan(sha, "any"))
            assertTrue(store.bookDirectory(sha).isDirectory)
        }

    /**
     * A truncated plan file would fail to parse and discard the listener's
     * rendered audio on the next open, so a write must never be observable
     * half-finished. Asserting the temporary sibling is gone is what proves the
     * rename happened rather than a direct write.
     */
    @Test
    fun `writes leave no temporary file behind`() = runBlocking {
        val store = store()
        store.savePlan(sha, largePlan(unitCount = 3))

        val leftovers = store.bookDirectory(sha).listFiles().orEmpty().filter { it.name.endsWith(".tmp") }
        assertTrue(leftovers.toString(), leftovers.isEmpty())
        assertNotNull(store.loadPlan(sha, null) as? PlanLoad.Loaded)
    }

    // endregion

    // region chapter audio and timelines

    /**
     * Timelines are chapter-relative and stored apart from the audio, which is
     * exactly what lets storage be reclaimed without losing the ability to follow
     * the text: the chapter drops to not-rendered but its timings remain.
     */
    @Test
    fun `deleting chapter audio keeps the chapter timeline`() = runBlocking {
        val store = store()
        val timings = listOf(ReaderTimingRange(0.0, 4.0, 0, 30))
        store.saveChapterTimeline(sha, chapterIndex = 2, timings = timings)
        store.chapterAudioFile(sha, 2).apply { parentFile?.mkdirs() }.writeBytes(ByteArray(64))

        assertEquals(64L, store.audioBytes(sha))
        assertTrue(store.deleteChapterAudio(sha, 2))

        assertEquals(0L, store.audioBytes(sha))
        assertEquals(timings, store.loadChapterTimeline(sha, 2))
    }

    /**
     * A `.partial` file is evidence that a render died mid-chapter. Encoder state
     * is gone, so the chapter has to restart from its first unit; sweeping on
     * worker start is what makes that hold across a killed process rather than
     * only across a clean cancellation.
     */
    @Test
    fun `partial audio is swept and reported by chapter index`() = runBlocking {
        val store = store()
        store.partialChapterAudioFile(sha, 5).apply { parentFile?.mkdirs() }.writeBytes(ByteArray(8))
        store.partialChapterAudioFile(sha, 1).writeBytes(ByteArray(8))
        store.chapterAudioFile(sha, 0).writeBytes(ByteArray(8))

        assertEquals(listOf(1, 5), store.sweepPartialAudio(sha))
        assertTrue(store.chapterAudioFile(sha, 0).isFile)
        assertEquals(8L, store.audioBytes(sha))
    }

    /** Partial files are scratch, so they must not be counted as reclaimable audio. */
    @Test
    fun `audio bytes excludes partial files`() = runBlocking {
        val store = store()
        store.chapterAudioFile(sha, 0).apply { parentFile?.mkdirs() }.writeBytes(ByteArray(100))
        store.partialChapterAudioFile(sha, 1).writeBytes(ByteArray(900))

        assertEquals(100L, store.audioBytes(sha))
    }

    @Test
    fun `deleting a book removes its whole directory`() = runBlocking {
        val store = store()
        store.savePlan(sha, largePlan(unitCount = 2))
        store.saveBookText(sha, "text")
        store.chapterAudioFile(sha, 0).apply { parentFile?.mkdirs() }.writeBytes(ByteArray(4))

        assertTrue(store.deleteBook(sha))
        assertTrue(!store.bookDirectory(sha).exists())
    }

    // endregion

    // region render queue behaviour

    /**
     * A chapter with no units has nothing to synthesise, so it must start
     * rendered. Otherwise the scheduler would pick it forever and the book would
     * never progress past a page of front matter.
     */
    @Test
    fun `queue starts unit free chapters as rendered`() {
        val plan = NarrationPlan(
            planVersion = NarrationPlan.PLAN_VERSION,
            inputs = inputs("hash"),
            chapters = listOf(
                NarrationChapter(0, "Front matter", 0, 10, emptyList()),
                NarrationChapter(1, "One", 10, 40, listOf(NarrationUnit(10, 40, "x".repeat(30)))),
            ),
        )

        val queue = RenderQueue.forPlan(plan)

        assertEquals(listOf(RenderState.RENDERED, RenderState.NOT_RENDERED), queue.states)
        assertEquals(1, queue.renderedCount)
    }

    /**
     * The window counts the contiguous run ahead of the playhead, not the total
     * rendered anywhere ahead. A gap is a wall the listener will hit, so counting
     * past it would satisfy the window on paper and stall playback in practice.
     */
    @Test
    fun `rendered run after the playhead stops at the first gap`() {
        val queue = RenderQueue(
            states = listOf(
                RenderState.RENDERED,
                RenderState.RENDERED,
                RenderState.RENDERED,
                RenderState.NOT_RENDERED,
                RenderState.RENDERED,
            ),
        )

        assertEquals(2, queue.renderedRunAfter(0))
        assertEquals(0, queue.renderedRunAfter(2))
        assertEquals(1, queue.renderedRunAfter(3))
    }

    /** Duration covers rendered chapters only, so it grows as rendering proceeds. */
    @Test
    fun `rendered duration ignores chapters that are not rendered`() {
        val queue = RenderQueue(
            states = listOf(RenderState.RENDERED, RenderState.NOT_RENDERED, RenderState.RENDERED),
            chapterDurationsMs = listOf(1_000L, 5_000L, 2_500L),
        )

        assertEquals(3_500L, queue.renderedDurationMs)
        assertTrue(!queue.isFullyRendered)
    }

    // endregion

    // region book text hash

    /**
     * The hash has to change when the extraction profile changes, or a plan built
     * by an older extraction would look current while its offsets referred to
     * differently trimmed text.
     */
    @Test
    fun `book text hash separates extraction versions`() {
        val text = "Once upon a time"

        assertEquals(
            NarrationStore.bookTextHash(text, 1),
            NarrationStore.bookTextHash(text, 1),
        )
        assertTrue(
            NarrationStore.bookTextHash(text, 1) != NarrationStore.bookTextHash(text, 2),
        )
        assertTrue(
            NarrationStore.bookTextHash(text, 1) != NarrationStore.bookTextHash(text + " ", 1),
        )
    }

    // endregion

    // region generators and fixtures

    private fun inputs(hash: String) = PlanInputs(
        sourceSha256 = sha,
        bookTextHash = hash,
        extractionVersion = 1,
        planVersion = NarrationPlan.PLAN_VERSION,
        synthesisInputLimit = 1_000,
        enabledEventKeys = listOf("profanity", "violence"),
        pronunciationRuleFingerprint = "none",
    )

    /**
     * Generates well-formed plans: offsets accumulate, so chapters and units are
     * ordered and non-overlapping. Serialisation would not care, but a generator
     * that produced nonsense would make a failure hard to read.
     */
    private fun planArb(): Arb<NarrationPlan> = Arb.list(Arb.int(0..4), 0..5).map { unitCounts ->
        var offset = 0
        val chapters = unitCounts.mapIndexed { chapterIndex, unitCount ->
            val start = offset
            val units = (0 until unitCount).map {
                val text = "u".repeat(5)
                val unit = NarrationUnit(offset, offset + text.length, text)
                offset += text.length + 1
                unit
            }
            if (unitCount == 0) offset += 1
            NarrationChapter(chapterIndex, "Chapter $chapterIndex", start, offset, units)
        }
        NarrationPlan(
            planVersion = NarrationPlan.PLAN_VERSION,
            inputs = inputs("hash-${unitCounts.joinToString("-")}"),
            chapterDerivationFellBackToSpine = unitCounts.size % 2 == 0,
            chapters = chapters,
        )
    }

    private fun timingArb(): Arb<ReaderTimingRange> =
        Arb.numericDouble(0.0, 100_000.0).map { start ->
            val startCharacter = (start * 10).toInt()
            ReaderTimingRange(
                startTime = start,
                endTime = start + 1.5,
                startCharacter = startCharacter,
                endCharacter = startCharacter + 40,
            )
        }

    private fun largePlan(unitCount: Int): NarrationPlan {
        val perChapter = 250
        var offset = 0
        val chapters = mutableListOf<NarrationChapter>()
        var remaining = unitCount
        var chapterIndex = 0
        while (remaining > 0) {
            val take = minOf(perChapter, remaining)
            val start = offset
            val units = (0 until take).map {
                val text = "Sentence number $it in chapter $chapterIndex, long enough to be real."
                val unit = NarrationUnit(offset, offset + text.length, text)
                offset += text.length + 1
                unit
            }
            chapters += NarrationChapter(chapterIndex, "Chapter $chapterIndex", start, offset, units)
            remaining -= take
            chapterIndex++
        }
        return NarrationPlan(
            planVersion = NarrationPlan.PLAN_VERSION,
            inputs = inputs("large-$unitCount"),
            chapters = chapters,
        )
    }

    // endregion
}
