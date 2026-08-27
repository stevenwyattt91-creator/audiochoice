package com.audiochoice.mobile.reader

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ReaderParagraphParserTest {

    /**
     * The load-bearing invariant. Every cached server alignment indexes into the
     * exact string EpubTextReader produced, so a paragraph's offsets must select
     * precisely its own text. If this ever drifts, filtered passages would be
     * masked in the wrong place.
     */
    @Test
    fun `offsets round-trip to the identical source substring`() {
        val text = "Chapter One\n\nHe opened the door.\nShe did not look up.\n\n\nThe end."
        val paragraphs = ReaderParagraphParser.parse(text)
        assertTrue(paragraphs.isNotEmpty())
        paragraphs.forEach { paragraph ->
            assertEquals(
                paragraph.text,
                text.substring(paragraph.startCharacter, paragraph.endCharacter),
            )
        }
    }

    @Test
    fun `splits on newline runs and drops blank blocks`() {
        val text = "First.\n\nSecond.\n\n\n\nThird."
        val paragraphs = ReaderParagraphParser.parse(text)
        assertEquals(listOf("First.", "Second.", "Third."), paragraphs.map { it.text })
    }

    @Test
    fun `paragraphs are ordered and non-overlapping`() {
        val text = "A one.\nB two.\n\nC three.\nD four."
        val paragraphs = ReaderParagraphParser.parse(text)
        paragraphs.zipWithNext().forEach { (earlier, later) ->
            assertTrue(earlier.endCharacter <= later.startCharacter)
        }
    }

    @Test
    fun `whitespace-only and empty input produce no paragraphs`() {
        assertEquals(emptyList<ReaderParagraph>(), ReaderParagraphParser.parse(""))
        assertEquals(emptyList<ReaderParagraph>(), ReaderParagraphParser.parse("\n\n   \n\t\n"))
    }

    @Test
    fun `indented text keeps offsets tight to the visible characters`() {
        val text = "   Indented line.   \n\nNext."
        val paragraphs = ReaderParagraphParser.parse(text)
        assertEquals("Indented line.", paragraphs.first().text)
        assertEquals(3, paragraphs.first().startCharacter)
        assertEquals(
            paragraphs.first().text,
            text.substring(paragraphs.first().startCharacter, paragraphs.first().endCharacter),
        )
    }

    @Test
    fun `indexOfCharacter finds the containing paragraph`() {
        val text = "First.\n\nSecond.\n\nThird."
        val paragraphs = ReaderParagraphParser.parse(text)
        assertEquals(0, paragraphs.indexOfCharacter(text.indexOf("First")))
        assertEquals(1, paragraphs.indexOfCharacter(text.indexOf("Second") + 2))
        assertEquals(2, paragraphs.indexOfCharacter(text.indexOf("Third")))
    }

    @Test
    fun `indexOfCharacter falls back to the preceding paragraph inside a gap`() {
        val text = "First.\n\nSecond."
        val paragraphs = ReaderParagraphParser.parse(text)
        // Index 6 and 7 are the separating newlines, which belong to no paragraph.
        assertEquals(0, paragraphs.indexOfCharacter(6))
        assertEquals(1, paragraphs.indexOfCharacter(text.length + 50))
        assertEquals(-1, emptyList<ReaderParagraph>().indexOfCharacter(3))
    }
}
