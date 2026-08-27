package com.audiochoice.mobile.reader

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ReaderMaskingTest {

    private fun paragraphsOf(text: String) = ReaderParagraphParser.parse(text)

    @Test
    fun `merged coalesces overlapping and touching ranges`() {
        val merged = listOf(
            ReaderMask(10, 20),
            ReaderMask(15, 25),
            ReaderMask(25, 30),
            ReaderMask(50, 60),
        ).merged()
        assertEquals(listOf(ReaderMask(10, 30), ReaderMask(50, 60)), merged)
    }

    @Test
    fun `no masks returns the paragraphs unchanged`() {
        val text = "First line.\n\nSecond line."
        val paragraphs = paragraphsOf(text)
        val display = readerDisplayParagraphs(paragraphs, emptyList())
        assertEquals(paragraphs.map { it.text }, display.map { it.displayText })
        assertTrue(display.none { it.hasRemovedText })
    }

    /**
     * The whole point of the change: filtered characters must be gone from the
     * rendered string, not merely painted over. The previous reader used a black
     * SpanStyle, which left the text present and reachable by screen readers and
     * any text-extraction path.
     */
    @Test
    fun `filtered text is absent from the rendered output`() {
        val text = "He said a terrible word aloud."
        val paragraphs = paragraphsOf(text)
        val start = text.indexOf("terrible word")
        val masks = listOf(ReaderMask(start, start + "terrible word".length)).merged()

        val display = readerDisplayParagraphs(paragraphs, masks)

        assertFalse(display.single().displayText.contains("terrible"))
        assertFalse(display.single().displayText.contains("word"))
        assertTrue(display.single().displayText.contains(READER_REMOVAL_MARKER))
        assertEquals(1, display.single().removedPassages)
    }

    @Test
    fun `surrounding text survives removal and spacing is normalised`() {
        val text = "Before the bad part after."
        val paragraphs = paragraphsOf(text)
        val start = text.indexOf("the bad part")
        val masks = listOf(ReaderMask(start, start + "the bad part".length)).merged()

        val display = readerDisplayParagraphs(paragraphs, masks).single()

        assertTrue(display.displayText.startsWith("Before"))
        assertTrue(display.displayText.endsWith("after."))
        assertFalse("no doubled spaces around the marker", display.displayText.contains("  "))
    }

    @Test
    fun `a mask spanning multiple paragraphs removes from each`() {
        val text = "Alpha one.\n\nBravo two.\n\nCharlie three."
        val paragraphs = paragraphsOf(text)
        val masks = listOf(
            ReaderMask(text.indexOf("one"), text.indexOf("two") + 3),
        ).merged()

        val display = readerDisplayParagraphs(paragraphs, masks)

        assertFalse(display[0].displayText.contains("one"))
        assertFalse(display[1].displayText.contains("Bravo"))
        assertFalse(display[1].displayText.contains("two"))
        // The untouched third paragraph must be byte-identical.
        assertEquals("Charlie three.", display[2].displayText)
        assertFalse(display[2].hasRemovedText)
    }

    @Test
    fun `a fully masked paragraph collapses to the marker`() {
        val text = "Keep this.\n\nRemove all of this."
        val paragraphs = paragraphsOf(text)
        val second = paragraphs[1]
        val masks = listOf(ReaderMask(second.startCharacter, second.endCharacter)).merged()

        val display = readerDisplayParagraphs(paragraphs, masks)

        assertEquals("Keep this.", display[0].displayText)
        assertEquals(READER_REMOVAL_MARKER, display[1].displayText)
    }

    @Test
    fun `multiple masks in one paragraph each leave a marker`() {
        val text = "one bad two bad three."
        val paragraphs = paragraphsOf(text)
        val first = text.indexOf("bad")
        val second = text.lastIndexOf("bad")
        val masks = listOf(
            ReaderMask(first, first + 3),
            ReaderMask(second, second + 3),
        ).merged()

        val display = readerDisplayParagraphs(paragraphs, masks).single()

        assertEquals(2, display.removedPassages)
        assertFalse(display.displayText.contains("bad"))
        assertTrue(display.displayText.contains("one"))
        assertTrue(display.displayText.contains("three."))
    }

    @Test
    fun `masks outside a paragraph do not affect it`() {
        val text = "Only paragraph."
        val paragraphs = paragraphsOf(text)
        val masks = listOf(ReaderMask(500, 520)).merged()
        val display = readerDisplayParagraphs(paragraphs, masks).single()
        assertEquals("Only paragraph.", display.displayText)
        assertFalse(display.hasRemovedText)
    }

    @Test
    fun `paragraph offsets are preserved for audio-follow after removal`() {
        val text = "Alpha.\n\nBravo bad words here."
        val paragraphs = paragraphsOf(text)
        val start = text.indexOf("bad words")
        val masks = listOf(ReaderMask(start, start + "bad words".length)).merged()

        val display = readerDisplayParagraphs(paragraphs, masks)

        // Display text shrank, but the source offsets must be untouched so a
        // position can still be mapped back to the right paragraph.
        assertEquals(paragraphs[1].startCharacter, display[1].paragraph.startCharacter)
        assertEquals(paragraphs[1].endCharacter, display[1].paragraph.endCharacter)
    }
}
