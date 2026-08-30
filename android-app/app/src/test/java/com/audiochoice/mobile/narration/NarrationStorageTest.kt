package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.NarrationChapter
import com.audiochoice.mobile.data.NarrationUnit
import com.audiochoice.mobile.data.RenderState
import com.audiochoice.mobile.data.VoiceKind
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import kotlinx.coroutines.runBlocking
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

class NarrationStorageTest {

    @get:Rule
    val temporaryFolder = TemporaryFolder()

    // region the estimate

    /**
     * Held against a real encode.
     *
     * The figures below were produced by synthesizing 1,080 characters of mixed narration and
     * dialogue through Amazon Polly's generative engine and measuring the resulting Opus file:
     * 207.3 bytes per character, averaged over three voices, on 2026-08-29.
     *
     * This test exists because the first version of the estimate reasoned from "150 words a
     * minute" and was wrong by a third. A ratio test against the code's own constants would have
     * passed on that wrong figure, so the comparison has to be against something measured.
     */
    @Test
    fun `the estimate lands within thirty percent of a real encode`() {
        val characters = 400_000
        val estimated = NarrationStorage.estimateBytes(characters, VoiceKind.PREMIUM)

        // Measured bytes per character, not derived from anything in the production code.
        val measuredBytes = (characters * 207.349).toLong()

        val ratio = estimated.toDouble() / measuredBytes
        assertTrue(
            "estimate $estimated against a measured $measuredBytes is a ratio of $ratio",
            ratio in 0.70..1.30,
        )
    }

    /**
     * The measured premium rate must stay tied to what was actually observed, and the estimated
     * on-device rates must stay in its neighbourhood rather than drifting back to a guess that
     * happens to sound reasonable.
     */
    @Test
    fun `the narration rates stay near the measured figure`() {
        // Ruth 17.60, Matthew 19.74, Danielle 16.61 -> 17.98 average.
        assertEquals(18.0, NarrationStorage.charactersPerSecond(VoiceKind.PREMIUM), 0.5)
        assertTrue(
            "only the premium rate has been measured; the others must not claim to be",
            NarrationStorage.rateIsMeasured(VoiceKind.PREMIUM),
        )
        // Samsung SM-S936U, Android 16: 18.4 characters per second spoken.
        assertEquals(18.4, NarrationStorage.charactersPerSecond(VoiceKind.SYSTEM), 0.5)
        assertTrue(
            "the device's own voice rate has been measured and must say so",
            NarrationStorage.rateIsMeasured(VoiceKind.SYSTEM),
        )
        // No on-device neural engine has been measured, so it must not claim to have been.
        assertFalse(
            "LOCAL_NEURAL has not been measured on any device",
            NarrationStorage.rateIsMeasured(VoiceKind.LOCAL_NEURAL),
        )
        assertTrue(
            "LOCAL_NEURAL at ${NarrationStorage.charactersPerSecond(VoiceKind.LOCAL_NEURAL)} is " +
                "implausibly far from the two measured rates",
            NarrationStorage.charactersPerSecond(VoiceKind.LOCAL_NEURAL) in 15.0..21.0,
        )
        // The two measured engines speak at nearly the same rate, which turned out to be a fact
        // about speech rather than a coincidence. A large gap would mean one was re-guessed.
        assertTrue(
            "the measured device and premium rates have drifted apart",
            kotlin.math.abs(
                NarrationStorage.charactersPerSecond(VoiceKind.SYSTEM) -
                    NarrationStorage.charactersPerSecond(VoiceKind.PREMIUM),
            ) < 2.0,
        )
    }

    /** The encoder is asked for more than it delivers, and the estimate must use what it delivers. */
    @Test
    fun `the estimate uses the effective bitrate rather than the requested one`() {
        assertTrue(
            NarrationStorage.EFFECTIVE_BITRATE_BITS_PER_SECOND <
                NarrationStorage.AUDIO_BITRATE_BITS_PER_SECOND,
        )
        // 29.7 kbps measured; 30,000 recorded.
        assertEquals(30_000L, NarrationStorage.EFFECTIVE_BITRATE_BITS_PER_SECOND)
    }

    /**
     * A novel's narration should come out in the low hundreds of megabytes at 32 kbps. Pinned
     * as an order of magnitude, because an estimate that came out in gigabytes or in kilobytes
     * would pass a ratio test against its own constants while being obviously wrong.
     */
    @Test
    fun `a novel estimates to a plausible number of megabytes`() {
        val megabytes = NarrationStorage.toMegabytes(
            NarrationStorage.estimateBytes(400_000, VoiceKind.SYSTEM),
        )
        // A real encode of a 400,000-character novel came to 79 MB.
        assertTrue("a 400,000-character book estimated at ${megabytes}MB", megabytes in 60..110)
    }

    @Test
    fun `an empty book estimates nothing`() {
        assertEquals(0L, NarrationStorage.estimateBytes(0, VoiceKind.SYSTEM))
        assertEquals(0L, NarrationStorage.estimateBytes(-5, VoiceKind.SYSTEM))
    }

    @Test
    fun `the estimate grows with the character count for every engine`() {
        VoiceKind.entries.forEach { kind ->
            assertTrue(
                "$kind did not scale",
                NarrationStorage.estimateBytes(200_000, kind) >
                    NarrationStorage.estimateBytes(100_000, kind),
            )
        }
    }

    /**
     * Only what is left to render is counted. Re-checking part-way through a book must not
     * demand room for audio that already exists on the device.
     */
    @Test
    fun `already rendered chapters are not counted again`() {
        val chapters = listOf(chapter(0, 1_000), chapter(1, 1_000), chapter(2, 1_000))
        val nothingDone = NarrationStorage.estimateRemainingBytes(
            chapters,
            List(3) { RenderState.NOT_RENDERED },
            VoiceKind.SYSTEM,
        )
        val twoDone = NarrationStorage.estimateRemainingBytes(
            chapters,
            listOf(RenderState.RENDERED, RenderState.RENDERED, RenderState.NOT_RENDERED),
            VoiceKind.SYSTEM,
        )
        assertEquals(nothingDone / 3, twoDone)
    }

    /**
     * The estimate counts the characters that will be spoken, not the characters in the book.
     * A book with heavy filtering renders far less audio than its length suggests.
     */
    @Test
    fun `the estimate counts spoken characters rather than book length`() {
        val filtered = NarrationChapter(
            index = 0, title = "One", startCharacter = 0, endCharacter = 10_000,
            units = listOf(unit(0, 100)),
        )
        assertEquals(100, NarrationStorage.spokenCharacters(filtered))
        assertEquals(
            NarrationStorage.estimateBytes(100, VoiceKind.SYSTEM),
            NarrationStorage.estimateRemainingBytes(
                listOf(filtered), listOf(RenderState.NOT_RENDERED), VoiceKind.SYSTEM,
            ),
        )
    }

    // endregion

    // region the reserve

    /**
     * A book that would fit only by eating into the reserve is refused up front, rather than
     * started and stopped part-way through. Being told before waiting is the whole point.
     */
    @Test
    fun `a book that fits only inside the reserve is refused`() {
        val estimate = 200L * 1_024 * 1_024
        val free = NarrationStorage.STORAGE_RESERVE_BYTES + estimate - 1

        val verdict = NarrationStorage.verdictFor(estimate, free)

        assertTrue(verdict is StorageVerdict.Insufficient)
        assertEquals(1L, (verdict as StorageVerdict.Insufficient).shortfallBytes)
    }

    @Test
    fun `a book that fits outside the reserve is allowed`() {
        val estimate = 200L * 1_024 * 1_024
        val verdict = NarrationStorage.verdictFor(
            estimate, NarrationStorage.STORAGE_RESERVE_BYTES + estimate,
        )
        assertTrue(verdict is StorageVerdict.Sufficient)
    }

    /** The shortfall is what the listener has to free, which is the number they can act on. */
    @Test
    fun `the shortfall reports what must be freed`() {
        val estimate = 500L * 1_024 * 1_024
        val free = NarrationStorage.STORAGE_RESERVE_BYTES + 100L * 1_024 * 1_024

        val insufficient = NarrationStorage.verdictFor(estimate, free)
            as StorageVerdict.Insufficient

        assertEquals(400L * 1_024 * 1_024, insufficient.shortfallBytes)
        assertEquals(400L, insufficient.shortfallMegabytes)
    }

    /** A shortfall must never be reported as a surplus, whatever the device reports as free. */
    @Test
    fun `a full device reports a shortfall rather than a negative one`() {
        val insufficient = NarrationStorage.verdictFor(1_024, freeBytes = 0)
            as StorageVerdict.Insufficient
        assertTrue(insufficient.shortfallBytes > 0)
    }

    @Test
    fun `rendering stops at or below the reserve`() {
        assertTrue(NarrationStorage.mustStopRendering(NarrationStorage.STORAGE_RESERVE_BYTES))
        assertTrue(NarrationStorage.mustStopRendering(NarrationStorage.STORAGE_RESERVE_BYTES - 1))
        assertFalse(NarrationStorage.mustStopRendering(NarrationStorage.STORAGE_RESERVE_BYTES + 1))
    }

    /**
     * Free space is measured inside a chapter, not only between chapters. A long chapter takes
     * minutes, and the renderer is not the only thing consuming space while it runs.
     */
    @Test
    fun `the in-chapter check interval is frequent enough to matter`() {
        assertTrue(NarrationStorage.FREE_SPACE_CHECK_INTERVAL_MS <= 30_000L)
        assertTrue(NarrationStorage.STOP_DEADLINE_MS <= 5_000L)
    }

    // endregion

    // region discard

    /**
     * The count is what the listener will have to wait for again, so it counts chapters that
     * have audio. A failed chapter needs rendering too, but has nothing to reclaim.
     */
    @Test
    fun `the discard estimate reports reclaimable bytes and the re-render count`() {
        val estimate = NarrationStorage.discardEstimate(
            audioBytes = 150L * 1_024 * 1_024,
            states = listOf(
                RenderState.RENDERED, RenderState.RENDERED,
                RenderState.RENDER_FAILED, RenderState.NOT_RENDERED,
            ),
        )
        assertEquals(150L, estimate.reclaimableMegabytes)
        assertEquals(2, estimate.chaptersNeedingRerender)
    }

    // endregion

    // region eviction

    /** Off unless a listener turns it on: someone who waited through a render wanted the audio. */
    @Test
    fun `two chapters are kept behind the playhead`() {
        val states = List(10) { RenderState.RENDERED }

        val evictable = NarrationEviction.evictableChapters(
            states, currentChapterIndex = 5, bookmarkedChapters = emptySet(),
        )

        // 5 is playing; 4 and 3 are kept; 2, 1 and 0 may go.
        assertEquals(listOf(0, 1, 2), evictable)
    }

    /**
     * A bookmark is a listener saying "come back here". Making them wait through a re-render to
     * honour it would be a poor answer to an explicit instruction.
     */
    @Test
    fun `a bookmarked chapter is never evicted`() {
        val states = List(10) { RenderState.RENDERED }

        val evictable = NarrationEviction.evictableChapters(
            states, currentChapterIndex = 8, bookmarkedChapters = setOf(2, 4),
        )

        assertFalse("a bookmarked chapter was evicted", evictable.contains(2))
        assertFalse("a bookmarked chapter was evicted", evictable.contains(4))
        assertEquals(listOf(0, 1, 3, 5), evictable)
    }

    @Test
    fun `nothing is evicted early in a book`() {
        val states = List(10) { RenderState.RENDERED }
        listOf(0, 1, 2).forEach { current ->
            assertTrue(
                "chapter $current evicted something",
                NarrationEviction.evictableChapters(states, current, emptySet()).isEmpty(),
            )
        }
    }

    /** Only rendered chapters have audio to delete. */
    @Test
    fun `unrendered chapters are not evictable`() {
        val states = listOf(
            RenderState.NOT_RENDERED, RenderState.RENDER_FAILED, RenderState.RENDERED,
            RenderState.RENDERED, RenderState.RENDERED, RenderState.RENDERED,
        )
        assertEquals(
            listOf(2),
            NarrationEviction.evictableChapters(states, currentChapterIndex = 5, emptySet()),
        )
    }

    /**
     * Bookmarks live in Book_Time and eviction works in chapters, so they are reconciled
     * through the timeline rather than guessed at from a duration.
     */
    @Test
    fun `bookmarks are mapped to chapters through the timeline`() {
        val locate: (Long) -> Pair<Int, Long>? = { bookTimeMs ->
            when {
                bookTimeMs < 60_000 -> 0 to bookTimeMs
                bookTimeMs < 120_000 -> 1 to (bookTimeMs - 60_000)
                else -> null
            }
        }

        val chapters = NarrationEviction.bookmarkedChapters(
            bookmarkTimesMs = listOf(1_000, 95_000, 500_000),
            locate = locate,
        )

        assertEquals(setOf(0, 1), chapters)
    }

    /** A bookmark past the rendered audio maps nowhere and must not protect chapter zero. */
    @Test
    fun `an unmappable bookmark protects nothing`() {
        val chapters = NarrationEviction.bookmarkedChapters(
            bookmarkTimesMs = listOf(9_999_999),
            locate = { null },
        )
        assertTrue(chapters.isEmpty())
    }

    // endregion

    // region the reserve stops a render rather than filling the device

    /**
     * A render refuses to start once free space has reached the reserve.
     *
     * Starting anyway would fill the device and then abandon a partial file — the worst of both
     * outcomes. Reported as its own stop reason rather than as a failure, because nothing is wrong
     * with the book or the voice and no retry can fix it.
     */
    @Test
    fun `a render stops at the reserve rather than filling the device`(): Unit = runBlocking {
        val store = NarrationStore(temporaryFolder.root)
        val coordinator = NarrationRenderCoordinator(
            store = store,
            engine = RefusingEngine,
            // Exactly at the reserve, which must already stop it.
            freeBytes = { NarrationStorage.STORAGE_RESERVE_BYTES },
        )

        val pass = coordinator.renderPending(
            sha256 = "abc", plan = onePlan(), filteredRanges = emptyList(),
        )

        assertEquals(StopReason.OUT_OF_STORAGE, pass.stopReason)
        assertEquals(
            "a chapter was rendered despite the reserve being reached",
            0,
            pass.queue.renderedCount,
        )
        assertEquals(
            "the engine was asked to synthesize with no room to write the result",
            0,
            RefusingEngine.calls,
        )
    }

    /**
     * A device that will not report its free space is not a device that is full.
     *
     * Refusing on "cannot tell" would make the feature unusable on such a device for no benefit.
     */
    @Test
    fun `an unreadable free-space reading does not stop a render`(): Unit = runBlocking {
        val coordinator = NarrationRenderCoordinator(
            store = NarrationStore(temporaryFolder.root),
            engine = RefusingEngine,
            freeBytes = { null },
        )

        val pass = coordinator.renderPending(
            sha256 = "abc", plan = onePlan(), filteredRanges = emptyList(),
        )

        assertFalse(
            "an unknown free-space reading was treated as a full device",
            pass.stopReason == StopReason.OUT_OF_STORAGE,
        )
    }

    @Test
    fun `ample free space does not stop a render`(): Unit = runBlocking {
        val coordinator = NarrationRenderCoordinator(
            store = NarrationStore(temporaryFolder.root),
            engine = RefusingEngine,
            freeBytes = { NarrationStorage.STORAGE_RESERVE_BYTES * 4 },
        )
        val pass = coordinator.renderPending(
            sha256 = "abc", plan = onePlan(), filteredRanges = emptyList(),
        )
        assertFalse(pass.stopReason == StopReason.OUT_OF_STORAGE)
    }

    // endregion

    // region the general purge must not reach narration audio

    /**
     * `purgeOrphanedAudioFiles` sweeps scratch directories for files no library row references.
     * Narration audio looks exactly like an orphan to it -- app-private, not referenced by any
     * audio row -- so `narration/` is deliberately absent from the list it walks.
     *
     * Worth pinning because adding a directory to that list is a one-line change that would
     * silently delete every rendered chapter on the device.
     */
    @Test
    fun `narration is outside the purgeable directories`() {
        // Read from source rather than referenced directly: the list lives in a private
        // companion, and widening visibility on a class the beta build depends on for the sake
        // of a test would be a worse trade than parsing three lines here.
        val source = LOCAL_AUDIO_STORE_PATHS.map { File(it) }.firstOrNull { it.isFile }
            ?.readText()
        assertTrue(
            "LocalAudioStore.kt was not found, so this guard would pass without checking",
            source != null,
        )
        val declaration = source!!.substringAfter("PURGEABLE_AUDIO_DIRECTORIES")
            .substringAfter("listOf(")
            .substringBefore(")")
        val directories = declaration.split(",")
            .map { it.trim().trim('"') }
            .filter { it.isNotEmpty() }

        assertTrue(
            "the purge list parsed as empty, so this guard is not testing anything",
            directories.isNotEmpty(),
        )
        assertFalse(
            "narration/ is swept by the orphan purge, which would delete rendered chapters",
            directories.contains(NarrationConfig.ROOT_DIRECTORY),
        )
        directories.forEach { directory ->
            assertFalse(
                "$directory would reach narration audio",
                directory.contains(NarrationConfig.ROOT_DIRECTORY, ignoreCase = true),
            )
        }
    }

    // endregion

    // region fixtures

    private companion object {
        val LOCAL_AUDIO_STORE_PATHS = listOf(
            "src/main/java/com/audiochoice/mobile/data/LocalAudioStore.kt",
            "app/src/main/java/com/audiochoice/mobile/data/LocalAudioStore.kt",
            "../app/src/main/java/com/audiochoice/mobile/data/LocalAudioStore.kt",
        )
    }

    /** A one-chapter plan, enough to see whether a render was attempted. */
    private fun onePlan() = com.audiochoice.mobile.data.NarrationPlan(
        planVersion = com.audiochoice.mobile.data.NarrationPlan.PLAN_VERSION,
        inputs = com.audiochoice.mobile.data.PlanInputs(
            sourceSha256 = "abc",
            bookTextHash = "hash",
            extractionVersion = 1,
            planVersion = com.audiochoice.mobile.data.NarrationPlan.PLAN_VERSION,
            synthesisInputLimit = SynthesisInputLimit.CEILING,
        ),
        chapterDerivationFellBackToSpine = false,
        chapters = listOf(chapter(0, 400)),
    )

    /**
     * Refuses every request and counts them.
     *
     * Counting is the point: the storage tests assert the engine was never *asked*, which is
     * stronger than asserting nothing was written.
     */
    private object RefusingEngine : com.audiochoice.mobile.narration.voice.VoiceEngine {
        var calls = 0
            private set

        override val kind = VoiceKind.SYSTEM
        override val voiceID = "test"
        override val maximumInputCharacters = 1_000

        override suspend fun renderChapter(
            request: com.audiochoice.mobile.narration.voice.ChapterRenderRequest,
        ): com.audiochoice.mobile.narration.voice.ChapterRenderOutcome {
            calls += 1
            return com.audiochoice.mobile.narration.voice.ChapterRenderOutcome.Failed(
                "the test engine never synthesizes", retryable = false,
            )
        }
    }

    private fun chapter(index: Int, characters: Int) = NarrationChapter(
        index = index,
        title = "Chapter $index",
        startCharacter = index * characters,
        endCharacter = (index + 1) * characters,
        units = listOf(unit(index * characters, characters)),
    )

    private fun unit(start: Int, length: Int) = NarrationUnit(
        startCharacter = start,
        endCharacter = start + length,
        sourceCharacters = "x".repeat(length),
    )
}
