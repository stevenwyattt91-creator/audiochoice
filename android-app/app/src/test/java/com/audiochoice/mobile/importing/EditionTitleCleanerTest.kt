package com.audiochoice.mobile.importing

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * Filename cleanup. The cleaner must never destroy an edition marker, because
 * part numbers are what distinguish two halves of the same audiobook, and beta
 * catalog matching reads the part out of the title.
 */
class EditionTitleCleanerTest {

    @Test
    fun `strips a junk code and the extension`() {
        assertEquals("fourth wingggg", EditionTitleCleaner.clean("fourth wingggg (3112r).mp3"))
    }

    /** Cleaning cannot fix a misspelling, and must not pretend to. */
    @Test
    fun `leaves the words themselves alone`() {
        assertEquals("fourth wingggg", EditionTitleCleaner.clean("fourth wingggg.mp3"))
    }

    @Test
    fun `keeps a spelled out part marker`() {
        assertEquals(
            "Fourth Wing (Part 1 of 2)",
            EditionTitleCleaner.clean("Fourth Wing (Part 1 of 2).m4b"),
        )
    }

    /**
     * A compact marker tokenizes as one letters-and-digits run, which is the same
     * shape as a junk code. Part information wins that tie.
     */
    @Test
    fun `keeps compact part markers`() {
        assertEquals("Iron Flame (1of2)", EditionTitleCleaner.clean("Iron Flame (1of2).m4b"))
        assertEquals("Iron Flame [Pt.2]", EditionTitleCleaner.clean("Iron Flame [Pt.2].m4b"))
    }

    @Test
    fun `keeps edition wording that matching depends on`() {
        assertEquals(
            "ACOTAR (Dramatized Adaptation)",
            EditionTitleCleaner.clean("ACOTAR (Dramatized Adaptation).m4b"),
        )
        assertEquals("Iron Flame [Unabridged]", EditionTitleCleaner.clean("Iron Flame [Unabridged].m4b"))
    }

    @Test
    fun `drops encoding details`() {
        assertEquals("King Sorrow", EditionTitleCleaner.clean("King_Sorrow_(128kbps).mp3"))
        assertEquals("King Sorrow", EditionTitleCleaner.clean("King Sorrow [64k stereo].mp3"))
    }

    @Test
    fun `drops a bare duplicate marker`() {
        assertEquals("King Sorrow", EditionTitleCleaner.clean("King Sorrow (2).m4b"))
    }

    @Test
    fun `removes a leading track number`() {
        assertEquals("Iron Flame", EditionTitleCleaner.clean("01 - Iron Flame.mp3"))
        assertEquals("Iron Flame", EditionTitleCleaner.clean("07. Iron Flame.mp3"))
    }

    @Test
    fun `expands dot separators only when there are no real spaces`() {
        assertEquals("the hobbit unabridged", EditionTitleCleaner.clean("the.hobbit.unabridged.mp3"))
        // A dot inside a spaced title is punctuation, not a separator.
        assertEquals("Iron Flame Vol. 2", EditionTitleCleaner.clean("Iron Flame Vol. 2.m4b"))
    }

    @Test
    fun `keeps unrecognised real words rather than guessing`() {
        assertEquals(
            "Iron Flame (Special Anniversary)",
            EditionTitleCleaner.clean("Iron Flame (Special Anniversary).m4b"),
        )
    }

    @Test
    fun `handles names with no extension`() {
        assertEquals("King Sorrow", EditionTitleCleaner.clean("King Sorrow"))
    }

    @Test
    fun `returns null when nothing usable survives`() {
        assertNull(EditionTitleCleaner.clean("(128kbps).mp3"))
        assertNull(EditionTitleCleaner.clean("   "))
    }
}
