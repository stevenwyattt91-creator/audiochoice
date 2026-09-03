package com.audiochoice.mobile.reader

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * Where the reader opens for a book whose EPUB was never aligned.
 *
 * Approximate by design, so the cases worth pinning are the ones where being wrong is not
 * approximate: landing at the start when someone is hours in, or past the end.
 */
class ApproximateReaderCharacterTest {
    @Test
    fun `two hours into ten lands a fifth of the way in`() {
        assertEquals(20_000, approximateReaderCharacter(7_200.0, 36_000.0, 100_000))
    }

    @Test
    fun `the very end of the audio lands at the end of the text, never past it`() {
        assertEquals(100_000, approximateReaderCharacter(36_000.0, 36_000.0, 100_000))
        assertEquals(100_000, approximateReaderCharacter(40_000.0, 36_000.0, 100_000))
    }

    @Test
    fun `nothing played means no guess at all`() {
        // Zero would be answered correctly by chance here, but the caller has to be able to tell
        // "the beginning" from "cannot say", because only one of those should move the reader.
        assertNull(approximateReaderCharacter(0.0, 36_000.0, 100_000))
    }

    @Test
    fun `a missing runtime or empty text means no guess`() {
        assertNull(approximateReaderCharacter(7_200.0, 0.0, 100_000))
        assertNull(approximateReaderCharacter(7_200.0, 36_000.0, 0))
    }

    @Test
    fun `a duration that is not a number is refused rather than producing an offset`() {
        assertNull(approximateReaderCharacter(7_200.0, Double.NaN, 100_000))
        assertNull(approximateReaderCharacter(Double.POSITIVE_INFINITY, 36_000.0, 100_000))
    }
}
