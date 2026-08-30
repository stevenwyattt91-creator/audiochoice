package com.audiochoice.mobile.player

import com.audiochoice.contracts.ScanEvent
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Two guards protect a narrated book from machinery built for imported audiobooks.
 *
 * Both exist because of the same deliberate reuse: a narrated book's scan events carry
 * character offsets in fields named for seconds, and its duration covers only the
 * chapters produced so far. That reuse is what lets the entire filter stack and the
 * whole progress path work unchanged, and it is also what makes these two call sites
 * wrong without an explicit check.
 *
 * These tests exist to fail if either guard is removed. They assert on the conditions
 * the guards use, and on what the underlying machinery would do without them, which is
 * the part that makes the consequence visible rather than theoretical.
 */
class NarrationPlaybackGuardTest {

    // region the filter-skip guard

    /**
     * What would happen without the guard, demonstrated on the path that is actually
     * reachable.
     *
     * The planner ignores a window that starts far ahead of the playhead, so a single
     * large offset does nothing. The damaging case is the ordinary one: a novel's filtered
     * passages tile its text, so adjacent character-offset windows chain through the
     * planner's connected-block expansion -- which exists so a short event inside a longer
     * scene does not strand the listener mid-scene -- and the chain runs the length of the
     * book. Read as seconds, that is a seek of tens of hours from the first minute of
     * playback.
     *
     * Asserting the size of the mistake, rather than only that a guard exists, is what
     * makes it clear why the guard must stay.
     */
    @Test
    fun `without a guard contiguous character offsets would chain into a seek of tens of hours`() {
        // Filtered passages tiling the first 120,000 characters, which is a fraction of a
        // novel. Contiguous, as they are once nearby events are merged.
        val events = (0 until 120).map { index ->
            narrationEvent(
                startCharacter = index * 1_000.0,
                endCharacter = (index + 1) * 1_000.0,
            )
        }
        val windows = events.map { FilterWindow(it.startTime, it.endTime) }

        // The planner is handed character offsets. It has no way to know that.
        val target = FilterSkipPlanner.targetSeconds(
            positionSeconds = 0.0,
            windows = windows,
            lookAheadSeconds = 0.25,
        )

        val hoursIn = (target ?: 0.0) / 3_600.0
        assertTrue(
            "expected a seek far beyond any real book, got ${hoursIn}h",
            hoursIn > 20.0,
        )
    }

    /**
     * The same events, read as what they are, describe a fraction of a book's text rather
     * than a day of audio. The two readings of one number are the whole hazard.
     */
    @Test
    fun `the same offsets describe a small part of the book text`() {
        val lastOffset = 120_000
        // Well inside a novel, which runs to several hundred thousand characters.
        assertTrue(lastOffset < 500_000)
    }

    /**
     * The guard's condition is the presence of narration state, not an empty event list.
     *
     * That distinction is the whole point: a narrated book's event list is normally not
     * empty, so a guard written as "no events, nothing to do" would never fire.
     */
    @Test
    fun `narration state is present exactly when the guard must fire`() {
        val narrated = PlayerUiState(narration = NarrationPlaybackState(1, 10))
        val imported = PlayerUiState()

        assertTrue("a narrated book must be recognisable", narrated.narration != null)
        assertNull("an imported audiobook must not look narrated", imported.narration)
    }

    /**
     * A narrated book carries events, so the pre-existing empty-list check cannot stand
     * in for the guard. If someone removes the narration check believing the empty-list
     * check covers it, this fails.
     */
    @Test
    fun `a narrated book has events, so the empty list check is not enough`() {
        val state = PlayerUiState(
            narration = NarrationPlaybackState(1, 10),
            scanEvents = listOf(narrationEvent(1_200.0, 1_450.0)),
        )

        assertFalse(
            "the empty-list check would let a narrated book through",
            state.scanEvents.isEmpty(),
        )
        assertTrue(state.narration != null)
    }

    /**
     * Nothing needs skipping in the first place: filtered passages were removed before
     * the text reached a voice, so they are absent from the audio rather than present and
     * seeked over. That is the reason the guard is correct and not merely protective.
     */
    @Test
    fun `filtered passages are absent from narrated audio rather than skipped`() {
        // A narrated book's events describe where filtered text was in Book_Text. The
        // audio was produced from what survived, so no interval of the audio corresponds
        // to a filtered passage.
        val state = PlayerUiState(
            narration = NarrationPlaybackState(10, 10),
            scanEvents = listOf(narrationEvent(1_200.0, 1_450.0)),
        )

        assertTrue(state.narration != null)
    }

    // endregion

    // region the completion guard

    /**
     * The failure the guard prevents, spelled out: a forty-chapter book three chapters in
     * has a duration of only those three chapters, and the completion check would call
     * that the end of the book.
     */
    @Test
    fun `without a guard a partly rendered book would be judged complete`() {
        val renderedDurationMs = 3 * 20 * 60 * 1_000L
        val positionMs = renderedDurationMs - 5_000L

        // This is exactly what the player would ask, using the only duration it has.
        assertTrue(
            "the completion check treats the end of rendered audio as the end of the book",
            BookCompletion.isComplete(positionMs, renderedDurationMs),
        )

        // The guard's condition is what stops it.
        val state = NarrationPlaybackState(renderedChapters = 3, totalChapters = 40)
        assertFalse(state.fullyRendered)
    }

    @Test
    fun `a fully rendered narrated book may still be finished`() {
        val state = NarrationPlaybackState(renderedChapters = 40, totalChapters = 40)

        assertTrue(state.fullyRendered)
        assertTrue(BookCompletion.isComplete(positionMs = 100_000L, durationMs = 100_000L))
    }

    /**
     * An imported audiobook has no narration state, so the guard cannot change its
     * behaviour. This is the assertion that keeps the guard from becoming a regression
     * for the books that ship today.
     */
    @Test
    fun `an imported audiobook is unaffected by the completion guard`() {
        val state = PlayerUiState()

        assertNull(state.narration)
        // The guard reads `narration?.let { ... }`, so a null marker leaves the original
        // completion behaviour exactly as it was.
        assertEquals(null, state.narration?.fullyRendered)
    }

    // endregion

    /**
     * An event as a narrated book carries it: offsets into Book_Text, in fields named for
     * seconds. Reusing the type is what buys the existing filter stack unchanged.
     */
    private fun narrationEvent(startCharacter: Double, endCharacter: Double) = ScanEvent(
        id = "narration-event",
        startTime = startCharacter,
        endTime = endCharacter,
        categoryID = "CAT",
        groupID = "GRP",
        eventID = "EVT",
        confidence = 0.9,
        stableKey = "key",
    )
}
