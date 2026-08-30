package com.audiochoice.mobile.narration

import com.audiochoice.contracts.ScanEvent
import com.audiochoice.mobile.data.NarrationChapter
import com.audiochoice.mobile.data.NarrationUnit
import com.audiochoice.mobile.data.RenderState
import com.audiochoice.mobile.data.VoiceKind
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class FilterChangeCoordinatorTest {

    // region identification

    /**
     * Only events whose enabled state actually changed matter. A listener switching a category
     * off and on again leaves nothing to do, and comparing before against after notices that.
     */
    @Test
    fun `switching a category off and back on affects nothing`() {
        val choices = FilterChoices(disabledGroupIDs = setOf(GROUP))
        assertTrue(
            FilterChangeCoordinator.affectedChapters(
                chapters(), allRendered(), events(), before = choices, after = choices,
            ).isEmpty(),
        )
    }

    /** A change to a category the book never triggered is a no-op. */
    @Test
    fun `a change touching no event in this book affects nothing`() {
        val impact = FilterChangeCoordinator.impactOf(
            chapters(), allRendered(), events(),
            before = FilterChoices(),
            after = FilterChoices(disabledGroupIDs = setOf(UNRELATED_GROUP)),
            voiceKind = VoiceKind.SYSTEM,
        )
        assertEquals(FilterChangeImpact.None, impact)
    }

    /** Only chapters overlapping a changed event, and only ones that have audio. */
    @Test
    fun `only rendered chapters overlapping a changed event are affected`() {
        // Events at 150 (chapter 0), 1_500 (chapter 1). Chapter 2 has none.
        val affected = FilterChangeCoordinator.affectedChapters(
            chapters(), allRendered(), events(),
            before = FilterChoices(),
            after = FilterChoices(disabledGroupIDs = setOf(GROUP)),
        )
        assertEquals(listOf(0, 1), affected)
    }

    /**
     * A chapter with no audio has nothing to discard and nothing to warn about, whatever it
     * overlaps.
     */
    @Test
    fun `an unrendered chapter is not affected`() {
        val states = listOf(RenderState.NOT_RENDERED, RenderState.RENDERED, RenderState.RENDERED)

        val affected = FilterChangeCoordinator.affectedChapters(
            chapters(), states, events(),
            before = FilterChoices(),
            after = FilterChoices(disabledGroupIDs = setOf(GROUP)),
        )

        assertEquals(listOf(1), affected)
    }

    /** A chapter mid-render is affected: its partial audio is already wrong. */
    @Test
    fun `a chapter being rendered right now is affected`() {
        val states = listOf(RenderState.RENDERING, RenderState.NOT_RENDERED, RenderState.NOT_RENDERED)

        val affected = FilterChangeCoordinator.affectedChapters(
            chapters(), states, events(),
            before = FilterChoices(),
            after = FilterChoices(disabledGroupIDs = setOf(GROUP)),
        )

        assertEquals(listOf(0), affected)
    }

    /**
     * An event ending exactly where a chapter begins belongs to the earlier chapter. Half-open
     * ranges everywhere, or a boundary event would re-render two chapters instead of one.
     */
    @Test
    fun `an event on a chapter boundary affects only the earlier chapter`() {
        val boundaryEvent = event("boundary", 900, 1_000)

        val affected = FilterChangeCoordinator.affectedChapters(
            chapters(), allRendered(), listOf(boundaryEvent),
            before = FilterChoices(),
            after = FilterChoices(disabledGroupIDs = setOf(GROUP)),
        )

        assertEquals(listOf(0), affected)
    }

    /** Enabling a previously disabled category is a change too, not only disabling one. */
    @Test
    fun `enabling a category is a change`() {
        val affected = FilterChangeCoordinator.affectedChapters(
            chapters(), allRendered(), events(),
            before = FilterChoices(disabledGroupIDs = setOf(GROUP)),
            after = FilterChoices(),
        )
        assertEquals(listOf(0, 1), affected)
    }

    // endregion

    // region what the listener is told

    @Test
    fun `the impact reports the chapter count and a whole-minute estimate`() {
        val impact = FilterChangeCoordinator.impactOf(
            chapters(), allRendered(), events(),
            before = FilterChoices(),
            after = FilterChoices(disabledGroupIDs = setOf(GROUP)),
            voiceKind = VoiceKind.SYSTEM,
        ) as FilterChangeImpact.Rerender

        assertEquals(2, impact.chapterCount)
        assertTrue("an estimate of ${impact.estimatedMinutes} minutes", impact.estimatedMinutes >= 1)
    }

    /**
     * The premium count is called out separately because it is the one part of a re-render with
     * a cost beyond waiting.
     */
    @Test
    fun `the premium resynthesis count is reported only for the premium voice`() {
        fun impactWith(kind: VoiceKind) = FilterChangeCoordinator.impactOf(
            chapters(), allRendered(), events(),
            before = FilterChoices(),
            after = FilterChoices(disabledGroupIDs = setOf(GROUP)),
            voiceKind = kind,
        ) as FilterChangeImpact.Rerender

        assertEquals(2, impactWith(VoiceKind.PREMIUM).chaptersResynthesizedByPremiumVoice)
        assertEquals(0, impactWith(VoiceKind.SYSTEM).chaptersResynthesizedByPremiumVoice)
        assertEquals(0, impactWith(VoiceKind.LOCAL_NEURAL).chaptersResynthesizedByPremiumVoice)
    }

    /** "This will take 0 minutes" followed by a wait is worse than saying one minute. */
    @Test
    fun `an estimate is never zero for real work`() {
        val tiny = listOf(chapter(0, 0, 20))
        assertEquals(1, FilterChangeCoordinator.estimatedMinutes(tiny, VoiceKind.SYSTEM))
    }

    @Test
    fun `nothing to render estimates nothing`() {
        assertEquals(0, FilterChangeCoordinator.estimatedMinutes(emptyList(), VoiceKind.SYSTEM))
    }

    /** A long book's estimate has to be plausible, not merely non-zero. */
    @Test
    fun `a long re-render estimates a plausible number of minutes`() {
        val long = listOf(chapter(0, 0, 200_000))
        val minutes = FilterChangeCoordinator.estimatedMinutes(long, VoiceKind.SYSTEM)
        assertTrue("200,000 characters estimated at $minutes minutes", minutes in 30..120)
    }

    // endregion

    // region playback during a re-render

    /**
     * The difference between a listener noticing nothing and their audio stopping. When the
     * chapter they are in is untouched, only a later playlist item changes.
     */
    @Test
    fun `playback continues when the current chapter is unaffected`() {
        assertTrue(FilterChangeCoordinator.playbackCanContinue(listOf(3, 4), currentChapterIndex = 1))
        assertFalse(FilterChangeCoordinator.playbackCanContinue(listOf(1, 4), currentChapterIndex = 1))
    }

    /**
     * Whoever is waiting is waiting for one chapter, and it is the one they are in. Rendering
     * from the start would make them wait through every earlier chapter first.
     */
    @Test
    fun `the listener's own chapter is rendered first`() {
        assertEquals(
            listOf(5, 1, 3, 8),
            FilterChangeCoordinator.renderOrder(listOf(8, 3, 1, 5), currentChapterIndex = 5),
        )
    }

    @Test
    fun `plan order is used when the current chapter is unaffected`() {
        assertEquals(
            listOf(1, 3, 8),
            FilterChangeCoordinator.renderOrder(listOf(8, 3, 1), currentChapterIndex = 5),
        )
    }

    // endregion

    // region restoring the position

    /**
     * Until every affected chapter before the target is rendered, the earlier chapters have no
     * durations and Book_Time for the target does not exist yet. This is why the position is
     * recorded in characters at confirmation: re-rendering changes durations, so the Book_Time
     * the listener was at stops denoting the same words.
     */
    @Test
    fun `the position waits for every affected chapter before it`() {
        val affected = listOf(1, 2, 5)

        val partly = listOf(
            RenderState.RENDERED, RenderState.RENDERED, RenderState.NOT_RENDERED,
            RenderState.RENDERED, RenderState.RENDERED, RenderState.NOT_RENDERED,
        )
        assertFalse(
            "the position was restored before chapter 2 was re-rendered",
            FilterChangeCoordinator.canRestorePosition(affected, partly, targetChapterIndex = 3),
        )

        val ready = partly.toMutableList().also { it[2] = RenderState.RENDERED }
        assertTrue(
            FilterChangeCoordinator.canRestorePosition(affected, ready, targetChapterIndex = 3),
        )
    }

    /** A later affected chapter does not hold up a position earlier in the book. */
    @Test
    fun `a later affected chapter does not delay an earlier position`() {
        val states = List(6) { RenderState.RENDERED }.toMutableList()
        states[5] = RenderState.NOT_RENDERED
        assertTrue(
            FilterChangeCoordinator.canRestorePosition(listOf(1, 5), states, targetChapterIndex = 2),
        )
    }

    // endregion

    // region deadlines

    @Test
    fun `the deadlines match what the listener is promised`() {
        assertTrue(FilterChangeCoordinator.IDENTIFICATION_DEADLINE_MS <= 2_000L)
        assertTrue(FilterChangeCoordinator.STOP_DEADLINE_MS <= 5_000L)
    }

    // endregion

    // region fixtures

    private fun chapters() = listOf(
        chapter(0, 0, 1_000),
        chapter(1, 1_000, 2_000),
        chapter(2, 2_000, 3_000),
    )

    private fun allRendered() = List(3) { RenderState.RENDERED }

    private fun chapter(index: Int, start: Int, end: Int) = NarrationChapter(
        index = index,
        title = "Chapter $index",
        startCharacter = start,
        endCharacter = end,
        units = listOf(
            NarrationUnit(
                startCharacter = start,
                endCharacter = end,
                sourceCharacters = "x".repeat(end - start),
            ),
        ),
    )

    private fun events() = listOf(event("one", 150, 200), event("two", 1_500, 1_560))

    private fun event(id: String, start: Int, end: Int) = ScanEvent(
        id = id,
        startTime = start.toDouble(),
        endTime = end.toDouble(),
        categoryID = CATEGORY,
        groupID = GROUP,
        eventID = EVENT,
        confidence = 1.0,
        stableKey = "stable-$id",
        safeDescription = "Something occurs",
    )

    private companion object {
        const val CATEGORY = "21000000-0000-0000-0000-000000000000"
        const val GROUP = "21000000-0000-0000-0000-000000000001"
        const val EVENT = "21000000-0000-0000-0000-000000000101"
        const val UNRELATED_GROUP = "41000000-0000-0000-0000-000000000002"
    }
}
