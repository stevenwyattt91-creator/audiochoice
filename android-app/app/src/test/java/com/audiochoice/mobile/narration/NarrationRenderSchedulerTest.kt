package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.RenderState
import com.audiochoice.mobile.data.RenderState.NOT_RENDERED
import com.audiochoice.mobile.data.RenderState.RENDERED
import com.audiochoice.mobile.data.RenderState.RENDERING
import com.audiochoice.mobile.data.RenderState.RENDER_FAILED
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The scheduler is a pure function, so the whole policy is a table. That is the
 * point of having pulled it out of the worker: these cases would otherwise need a
 * `WorkManager`, a synthesis engine and a media session to reach.
 */
class NarrationRenderSchedulerTest {

    private data class Case(
        val name: String,
        val states: List<RenderState>,
        val playhead: Int,
        val window: Int,
        val fullBook: Boolean = false,
        val paused: Boolean = false,
        val expected: Int?,
    )

    @Test
    fun `the scheduler chooses the same chapter for the same situation`() {
        val cases = listOf(
            Case(
                name = "nothing rendered yet starts at the playhead",
                states = listOf(NOT_RENDERED, NOT_RENDERED, NOT_RENDERED),
                playhead = 0,
                window = 2,
                expected = 0,
            ),
            Case(
                name = "window not yet satisfied continues forward",
                states = listOf(RENDERED, NOT_RENDERED, NOT_RENDERED),
                playhead = 0,
                window = 2,
                expected = 1,
            ),
            Case(
                name = "window exactly satisfied stops",
                states = listOf(RENDERED, RENDERED, RENDERED, NOT_RENDERED),
                playhead = 0,
                window = 2,
                expected = null,
            ),
            Case(
                name = "window over-satisfied stops",
                states = listOf(RENDERED, RENDERED, RENDERED, RENDERED),
                playhead = 0,
                window = 2,
                expected = null,
            ),
            Case(
                // The case the contiguous rule exists for. Two chapters are rendered
                // ahead, but not adjacently, so the listener hits the gap at chapter 1.
                name = "a gap ahead of the playhead does not count toward the window",
                states = listOf(RENDERED, NOT_RENDERED, RENDERED, RENDERED),
                playhead = 0,
                window = 2,
                expected = 1,
            ),
            Case(
                name = "a failed chapter is stepped past rather than retried",
                states = listOf(RENDERED, RENDER_FAILED, NOT_RENDERED, NOT_RENDERED),
                playhead = 0,
                window = 2,
                expected = 2,
            ),
            Case(
                name = "a failed chapter also blocks the contiguous run",
                states = listOf(RENDERED, RENDER_FAILED, RENDERED, RENDERED),
                playhead = 0,
                window = 1,
                expected = null,
            ),
            Case(
                name = "a chapter already rendering is not chosen again",
                states = listOf(RENDERED, RENDERING, NOT_RENDERED),
                playhead = 0,
                window = 3,
                expected = 2,
            ),
            Case(
                name = "chapters behind the playhead are left alone",
                states = listOf(NOT_RENDERED, NOT_RENDERED, RENDERED, NOT_RENDERED),
                playhead = 2,
                window = 2,
                expected = 3,
            ),
            Case(
                name = "a pause request renders nothing",
                states = listOf(NOT_RENDERED, NOT_RENDERED),
                playhead = 0,
                window = 5,
                paused = true,
                expected = null,
            ),
            Case(
                name = "a pause request outranks a full book request",
                states = listOf(NOT_RENDERED, NOT_RENDERED),
                playhead = 0,
                window = 5,
                fullBook = true,
                paused = true,
                expected = null,
            ),
            Case(
                name = "a full book request ignores the window",
                states = listOf(RENDERED, RENDERED, RENDERED, NOT_RENDERED),
                playhead = 0,
                window = 1,
                fullBook = true,
                expected = 3,
            ),
            Case(
                // Full-book rendering is for offline listening, so it fills in behind
                // the playhead too rather than leaving holes the listener cannot revisit.
                name = "a full book request also fills in behind the playhead",
                states = listOf(NOT_RENDERED, RENDERED, RENDERED),
                playhead = 2,
                window = 1,
                fullBook = true,
                expected = 0,
            ),
            Case(
                name = "a fully rendered book has nothing to do",
                states = listOf(RENDERED, RENDERED),
                playhead = 0,
                window = 5,
                fullBook = true,
                expected = null,
            ),
            Case(
                name = "an empty book has nothing to do",
                states = emptyList(),
                playhead = 0,
                window = 2,
                expected = null,
            ),
            Case(
                name = "a window below the floor still renders the next chapter",
                states = listOf(RENDERED, NOT_RENDERED),
                playhead = 0,
                window = 0,
                expected = 1,
            ),
            Case(
                // A nonsense playhead is clamped to the last chapter rather than
                // throwing, and that chapter is then the one to produce. A crash here
                // would be a crash during ordinary playlist churn.
                name = "a playhead past the end clamps and renders the last chapter",
                states = listOf(RENDERED, NOT_RENDERED),
                playhead = 99,
                window = 2,
                expected = 1,
            ),
            Case(
                name = "a negative playhead clamps to the first chapter",
                states = listOf(NOT_RENDERED, NOT_RENDERED),
                playhead = -5,
                window = 1,
                expected = 0,
            ),
        )

        cases.forEach { case ->
            assertEquals(
                case.name,
                case.expected,
                NarrationRenderScheduler.nextChapterToRender(
                    states = case.states,
                    playheadChapter = case.playhead,
                    renderAheadWindow = case.window,
                    fullBookRequested = case.fullBook,
                    pausedByListener = case.paused,
                ),
            )
        }
    }

    /**
     * Contiguous, not total. Counting past a gap would report the window satisfied
     * while playback stalls a few minutes later, which is the failure the listener
     * experiences and the code does not.
     */
    @Test
    fun `ready ahead stops at the first chapter that is not rendered`() {
        val states = listOf(RENDERED, RENDERED, RENDERED, NOT_RENDERED, RENDERED, RENDERED)

        assertEquals(2, NarrationRenderScheduler.readyAhead(states, 0))
        assertEquals(1, NarrationRenderScheduler.readyAhead(states, 1))
        assertEquals(0, NarrationRenderScheduler.readyAhead(states, 2))
        assertEquals(2, NarrationRenderScheduler.readyAhead(states, 3))
        assertEquals(0, NarrationRenderScheduler.readyAhead(states, 5))
    }

    @Test
    fun `work remaining ignores failed and rendering chapters`() {
        assertTrue(NarrationRenderScheduler.hasWorkRemaining(listOf(RENDERED, NOT_RENDERED)))
        assertFalse(NarrationRenderScheduler.hasWorkRemaining(listOf(RENDERED, RENDER_FAILED)))
        assertFalse(NarrationRenderScheduler.hasWorkRemaining(listOf(RENDERING, RENDERED)))
        assertFalse(NarrationRenderScheduler.hasWorkRemaining(emptyList()))
    }

    /**
     * A book that cannot be finished with the current voice should say so, rather
     * than leaving a queue that will never move.
     */
    @Test
    fun `a book is stalled only when everything was attempted and something failed`() {
        assertTrue(
            NarrationRenderScheduler.isStalledByFailures(listOf(RENDERED, RENDER_FAILED)),
        )
        assertTrue(
            NarrationRenderScheduler.isStalledByFailures(listOf(RENDER_FAILED, RENDER_FAILED)),
        )
        // Still work to do, so not stalled.
        assertFalse(
            NarrationRenderScheduler.isStalledByFailures(listOf(RENDER_FAILED, NOT_RENDERED)),
        )
        // Something is in flight, so not stalled.
        assertFalse(
            NarrationRenderScheduler.isStalledByFailures(listOf(RENDER_FAILED, RENDERING)),
        )
        assertFalse(NarrationRenderScheduler.isStalledByFailures(listOf(RENDERED, RENDERED)))
        assertFalse(NarrationRenderScheduler.isStalledByFailures(emptyList()))
    }

    // region the window value

    /**
     * The window is derived from a measured rate, not chosen. Until the measurement
     * exists the floor is used, which is the least that can work rather than a guess
     * dressed up as a default.
     */
    @Test
    fun `the default window is the floor rather than an estimate`() {
        assertEquals(RenderAheadWindow.MINIMUM, RenderAheadWindow.DEFAULT.chapters)
    }

    @Test
    fun `the window widens as measured synthesis slows`() {
        // Comfortably faster than the fastest playback: one chapter of slack.
        assertEquals(2, RenderAheadWindow.fromMeasuredRate(10.0, 2.0).chapters)
        // At exactly the threshold a device must clear to be offered at all.
        assertEquals(2, RenderAheadWindow.fromMeasuredRate(3.0, 2.0).chapters)
        // Barely keeping up needs more slack.
        assertEquals(3, RenderAheadWindow.fromMeasuredRate(1.0, 2.0).chapters)
        // Slower than playback cannot keep up at all, and gets the widest window.
        assertTrue(RenderAheadWindow.fromMeasuredRate(0.5, 2.0).chapters >= 4)
    }

    @Test
    fun `an unmeasured rate falls back to the floor`() {
        assertEquals(RenderAheadWindow.DEFAULT, RenderAheadWindow.fromMeasuredRate(0.0, 2.0))
        assertEquals(RenderAheadWindow.DEFAULT, RenderAheadWindow.fromMeasuredRate(-1.0, 2.0))
    }

    @Test
    fun `a window below the floor is rejected rather than silently corrected`() {
        runCatching { RenderAheadWindow(0) }.also { assertTrue(it.isFailure) }
    }

    // endregion
}
