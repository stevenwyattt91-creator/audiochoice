package com.audiochoice.mobile.reader

import com.audiochoice.mobile.data.ReaderTimingRange
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ReaderSyncTest {

    // Two aligned ranges with a deliberate hole between 20s and 30s, mirroring
    // the sparse coverage the server actually produces.
    private val timings = listOf(
        ReaderTimingRange(startTime = 10.0, endTime = 20.0, startCharacter = 100, endCharacter = 200),
        ReaderTimingRange(startTime = 30.0, endTime = 40.0, startCharacter = 300, endCharacter = 400),
    )

    @Test
    fun `character for time interpolates inside a range`() {
        assertEquals(100, readerCharacterForTime(timings, 10.0))
        assertEquals(150, readerCharacterForTime(timings, 15.0))
        assertEquals(350, readerCharacterForTime(timings, 35.0))
    }

    /**
     * The graceful-degradation contract. Returning null lets the reader hold its
     * previous highlight instead of snapping to the start of the book whenever
     * narration crosses an unaligned passage.
     */
    @Test
    fun `character for time returns null inside a coverage gap`() {
        assertNull(readerCharacterForTime(timings, 25.0))
        assertNull(readerCharacterForTime(timings, 0.0))
        assertNull(readerCharacterForTime(timings, 99.0))
        assertNull(readerCharacterForTime(emptyList(), 15.0))
    }

    @Test
    fun `range end is exclusive so adjacent ranges cannot both match`() {
        val adjacent = listOf(
            ReaderTimingRange(0.0, 10.0, 0, 100),
            ReaderTimingRange(10.0, 20.0, 100, 200),
        )
        assertEquals(100, readerCharacterForTime(adjacent, 10.0))
    }

    @Test
    fun `time for character interpolates inside a range`() {
        assertEquals(10.0, readerTimeForCharacter(timings, 100)!!, 0.001)
        assertEquals(15.0, readerTimeForCharacter(timings, 150)!!, 0.001)
        assertEquals(35.0, readerTimeForCharacter(timings, 350)!!, 0.001)
    }

    /** Tapping unaligned text should still move the audio, not silently no-op. */
    @Test
    fun `time for character falls forward to the next aligned range`() {
        assertEquals(30.0, readerTimeForCharacter(timings, 250)!!, 0.001)
        assertEquals(10.0, readerTimeForCharacter(timings, 0)!!, 0.001)
    }

    @Test
    fun `time for character returns null past the last aligned range`() {
        assertNull(readerTimeForCharacter(timings, 5_000))
        assertNull(readerTimeForCharacter(emptyList(), 100))
    }

    @Test
    fun `round trip through both directions stays within the same range`() {
        val character = readerCharacterForTime(timings, 17.5)!!
        val seconds = readerTimeForCharacter(timings, character)!!
        assertTrue("expected to land back inside 10..20 but was $seconds", seconds in 10.0..20.0)
    }

    @Test
    fun `zero length range does not divide by zero`() {
        val degenerate = listOf(ReaderTimingRange(5.0, 5.0, 10, 10))
        // Start == end makes the range empty, so nothing can be inside it.
        assertNull(readerCharacterForTime(degenerate, 5.0))
    }
}
