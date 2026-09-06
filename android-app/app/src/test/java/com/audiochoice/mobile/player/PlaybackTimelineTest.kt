package com.audiochoice.mobile.player

import io.kotest.property.Arb
import io.kotest.property.PropTestConfig
import io.kotest.property.arbitrary.int
import io.kotest.property.arbitrary.long
import io.kotest.property.checkAll
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * Regression tests for books people are already listening to.
 *
 * [DirectPlaybackTimeline] exists so the player reads position and duration through one
 * indirection rather than converting at each call site. That is only safe if it stays
 * indistinguishable from reading the controller directly, so what follows is deliberately
 * boring identity assertions.
 */
class PlaybackTimelineTest {

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
}
