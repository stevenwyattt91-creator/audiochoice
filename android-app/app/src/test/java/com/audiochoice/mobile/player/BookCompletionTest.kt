package com.audiochoice.mobile.player

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * When a book counts as finished.
 *
 * The boundaries are the whole of it. Too strict and nothing is ever marked finished, because
 * listeners stop before the credits; too loose and a book is marked finished the moment it
 * opens, while the duration is still unknown.
 *
 * These values are duplicated in the iOS client on purpose, so a book that reads as finished
 * on one platform reads the same on the other. If they are changed here they have to change
 * there too.
 */
class BookCompletionTest {

    @Test
    fun `an unprepared player is not finished`() {
        // Duration is 0 until the media item is prepared, and position is 0 with it.
        assertFalse(BookCompletion.isComplete(positionMs = 0, durationMs = 0))
    }

    @Test
    fun `a position without a duration is not finished`() {
        assertFalse(BookCompletion.isComplete(positionMs = 500_000, durationMs = 0))
    }

    @Test
    fun `the start of a book is not finished`() {
        assertFalse(BookCompletion.isComplete(positionMs = 0, durationMs = 36_000_000))
    }

    @Test
    fun `the middle of a book is not finished`() {
        assertFalse(BookCompletion.isComplete(positionMs = 18_000_000, durationMs = 36_000_000))
    }

    @Test
    fun `the very end is finished`() {
        assertTrue(BookCompletion.isComplete(positionMs = 36_000_000, durationMs = 36_000_000))
    }

    @Test
    fun `within the final seconds is finished`() {
        // Books end with credits and an outro that listeners stop before, so requiring the
        // final sample would mean nothing was ever complete.
        assertTrue(BookCompletion.isComplete(positionMs = 35_975_000, durationMs = 36_000_000))
    }

    @Test
    fun `a minute from the end is not finished`() {
        assertFalse(BookCompletion.isComplete(positionMs = 35_940_000, durationMs = 36_000_000))
    }

    @Test
    fun `a short file is not finished at its start`() {
        // Thirty seconds from a twenty second file is a negative threshold, which would have
        // marked it finished on sight.
        assertFalse(BookCompletion.isComplete(positionMs = 500, durationMs = 20_000))
    }

    @Test
    fun `a short file is not finished mid-way`() {
        assertFalse(BookCompletion.isComplete(positionMs = 10_000, durationMs = 20_000))
    }

    @Test
    fun `a short file is finished at its end`() {
        assertTrue(BookCompletion.isComplete(positionMs = 20_000, durationMs = 20_000))
    }

    @Test
    fun `the threshold is never negative`() {
        assertTrue(BookCompletion.thresholdMs(20_000) > 0)
        assertTrue(BookCompletion.thresholdMs(1_000) > 0)
    }

    @Test
    fun `a long book uses the fixed remainder rather than the fraction`() {
        // 98% of ten hours is twelve minutes from the end, which is far too generous.
        val duration = 36_000_000L
        assertEquals(duration - BookCompletion.REMAINING_MS, BookCompletion.thresholdMs(duration))
    }

    @Test
    fun `no duration means nothing is complete`() {
        assertEquals(Long.MAX_VALUE, BookCompletion.thresholdMs(0))
    }
}
