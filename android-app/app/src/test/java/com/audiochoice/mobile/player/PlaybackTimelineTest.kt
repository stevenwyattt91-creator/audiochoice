package com.audiochoice.mobile.player

import com.audiochoice.mobile.narration.NarrationTimeline
import io.kotest.property.Arb
import io.kotest.property.PropTestConfig
import io.kotest.property.arbitrary.int
import io.kotest.property.arbitrary.long
import io.kotest.property.checkAll
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * These are regression tests for books people are already listening to.
 *
 * The narration feature routes every position and duration read in the player through
 * one indirection. That is only safe if the imported-audiobook implementation is
 * indistinguishable from reading the controller directly, so most of what follows is
 * identity assertions -- deliberately boring, and the point.
 */
class PlaybackTimelineTest {

    // region imported audiobooks are unchanged

    /**
     * Position is passed straight through. A single-item playlist reports a position in
     * the book already, so any arithmetic here would be a bug.
     */
    @Test
    fun `direct timeline reports the controller position unchanged`(): Unit = runBlocking {
        checkAll(
            PropTestConfig(iterations = 300),
            Arb.int(0..8),
            Arb.long(0L..40_000_000L),
        ) { itemIndex, position ->
            assertEquals(position, DirectPlaybackTimeline.bookPositionMs(itemIndex, position))
        }
    }

    /**
     * Duration is passed through including the not-yet-known sentinel.
     *
     * Media3 reports `C.TIME_UNSET` before a duration is available, and callers already
     * treat a non-positive duration as unknown. Normalising it here would turn "unknown"
     * into "zero", which reads as a finished book.
     */
    @Test
    fun `direct timeline reports the controller duration unchanged including time unset`() {
        assertEquals(0L, DirectPlaybackTimeline.bookDurationMs(0L))
        assertEquals(1_234L, DirectPlaybackTimeline.bookDurationMs(1_234L))
        assertEquals(Long.MIN_VALUE + 1, DirectPlaybackTimeline.bookDurationMs(Long.MIN_VALUE + 1))
    }

    /**
     * A seek asks for no item index, which is the same `seekTo(position)` call the player
     * made before this indirection existed.
     */
    @Test
    fun `direct timeline seeks within the current item exactly as before`(): Unit = runBlocking {
        checkAll(PropTestConfig(iterations = 300), Arb.long(0L..40_000_000L)) { position ->
            val target = DirectPlaybackTimeline.seekTarget(position)
            assertNull("an imported audiobook must never seek by item index", target.itemIndex)
            assertEquals(position, target.positionMs)
        }
    }

    // endregion

    // region narrated books

    @Test
    fun `narration timeline accumulates position across chapters`() {
        val timeline = narrationTimeline(10_000L, 5_000L, 20_000L)

        assertEquals(0L, timeline.bookPositionMs(0, 0L))
        assertEquals(4_000L, timeline.bookPositionMs(0, 4_000L))
        assertEquals(12_000L, timeline.bookPositionMs(1, 2_000L))
        assertEquals(18_000L, timeline.bookPositionMs(2, 3_000L))
    }

    /**
     * The controller's per-item duration is discarded. It describes one chapter's file,
     * and reporting it as the book's length would make a long novel look like a
     * twenty-minute one.
     */
    @Test
    fun `narration timeline reports the book duration rather than the item duration`() {
        val timeline = narrationTimeline(10_000L, 5_000L)

        assertEquals(15_000L, timeline.bookDurationMs(itemDurationMs = 10_000L))
        assertEquals(15_000L, timeline.bookDurationMs(itemDurationMs = 999L))
    }

    @Test
    fun `narration timeline seeks by item index and offset`() {
        val timeline = narrationTimeline(10_000L, 5_000L, 20_000L)

        assertEquals(SeekTarget(0, 0L), timeline.seekTarget(0L))
        assertEquals(SeekTarget(1, 1_000L), timeline.seekTarget(11_000L))
        assertEquals(SeekTarget(2, 5_000L), timeline.seekTarget(20_000L))
    }

    /**
     * Position and seek invert each other. If they drift, a resume lands somewhere other
     * than where the listener stopped.
     */
    @Test
    fun `narration position and seek invert each other`(): Unit = runBlocking {
        val timeline = narrationTimeline(10_000L, 5_000L, 20_000L)

        checkAll(PropTestConfig(iterations = 300), Arb.long(0L..35_000L)) { bookTime ->
            val target = timeline.seekTarget(bookTime)
            val back = timeline.bookPositionMs(target.itemIndex!!, target.positionMs)
            assertEquals(bookTime, back)
        }
    }

    @Test
    fun `narration seeks are clamped to what has been rendered`() {
        val timeline = narrationTimeline(10_000L, 5_000L)

        assertEquals(SeekTarget(0, 0L), timeline.seekTarget(-5_000L))
        assertEquals(SeekTarget(1, 5_000L), timeline.seekTarget(999_999L))
    }

    @Test
    fun `a narrated book with nothing rendered reports no duration`() {
        val timeline = NarrationPlaybackTimeline(NarrationTimeline.EMPTY)

        assertEquals(0L, timeline.bookDurationMs(10_000L))
        assertEquals(0L, timeline.bookPositionMs(0, 5_000L))
        assertEquals(SeekTarget(0, 0L), timeline.seekTarget(5_000L))
    }

    // endregion

    // region the completion marker

    /**
     * The marker the completion guard tests on. A book is only finishable once every
     * chapter exists, because until then its duration is still growing.
     */
    @Test
    fun `narration state reports fully rendered only when every chapter exists`() {
        assertTrue(NarrationPlaybackState(renderedChapters = 4, totalChapters = 4).fullyRendered)
        assertTrue(!NarrationPlaybackState(renderedChapters = 3, totalChapters = 40).fullyRendered)
        // An empty book is not "fully rendered": there is nothing to finish.
        assertTrue(!NarrationPlaybackState(renderedChapters = 0, totalChapters = 0).fullyRendered)
    }

    @Test
    fun `narration state reports whether chapters remain`() {
        assertTrue(NarrationPlaybackState(3, 40).hasChaptersRemaining)
        assertTrue(!NarrationPlaybackState(40, 40).hasChaptersRemaining)
    }

    // endregion

    private fun narrationTimeline(vararg durationsMs: Long) = NarrationPlaybackTimeline(
        NarrationTimeline.of(
            renderedPlanIndices = durationsMs.indices.toList(),
            durationsMs = { index -> durationsMs[index] },
            timings = { emptyList() },
        ),
    )
}
