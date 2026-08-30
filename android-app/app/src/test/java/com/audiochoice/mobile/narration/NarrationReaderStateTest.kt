package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.NarrationPlan
import com.audiochoice.mobile.data.ReaderTimingRange
import com.audiochoice.mobile.data.SourceRange
import com.audiochoice.mobile.reader.EpubTextReader
import com.audiochoice.mobile.reader.ReaderMask
import com.audiochoice.mobile.reader.ReaderParagraphParser
import io.kotest.property.Arb
import io.kotest.property.PropTestConfig
import io.kotest.property.arbitrary.int
import io.kotest.property.arbitrary.list
import io.kotest.property.checkAll
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

class NarrationReaderStateTest {

    // region coverage

    /**
     * Narration coverage is total over rendered prose, which is the structural difference
     * from an imported audiobook.
     *
     * Reader alignment for an audiobook is deliberately sparse: the server skips any
     * transcript segment it cannot confidently anchor. Narration knew the offsets before
     * the audio existed, so every unit that was spoken has a range, and a gap in coverage
     * now means something specific rather than "the aligner was unsure".
     */
    @Test
    fun `every spoken unit has exactly one timing range`(): Unit = runBlocking {
        checkAll(PropTestConfig(iterations = 120), Arb.list(Arb.int(1..4), 1..4)) { paragraphCounts ->
            val document = documentOf(paragraphCounts)
            val plan = StructureParser.buildPlan(document, SHA, 1_000) ?: return@checkAll

            plan.chapters.forEach { chapter ->
                // What the renderer would produce for this chapter with nothing filtered.
                val speech = SpokenTextBuilder.build(chapter.units, emptyList())
                assertEquals(chapter.units.size, speech.spoken.size)
            }
        }
    }

    /** A filtered unit contributes no range, so coverage matches what was actually said. */
    @Test
    fun `a filtered unit contributes no timing range`(): Unit = runBlocking {
        val document = documentOf(listOf(2))
        val plan = StructureParser.buildPlan(document, SHA, 1_000)!!
        val units = plan.chapters.single().units
        val first = units.first()

        val speech = SpokenTextBuilder.build(
            units,
            listOf(ReaderMask(first.startCharacter, first.endCharacter)),
        )

        assertEquals(units.size - 1, speech.spoken.size)
    }

    // endregion

    // region masking

    /**
     * A paragraph a filter covered entirely renders not at all. An empty row would show
     * the listener that something was removed here, which is the opposite of the point of
     * removing it.
     */
    @Test
    fun `a fully filtered paragraph is not rendered at all`() {
        val bookText = "First paragraph survives.\n\nSecond paragraph is filtered.\n\nThird survives."
        val paragraphs = ReaderParagraphParser.parse(bookText)
        val second = paragraphs[1]

        val view = NarrationReaderState.derive(
            bookText = bookText,
            filteredRanges = listOf(ReaderMask(second.startCharacter, second.endCharacter)),
            narrationTimingRanges = emptyList(),
        )

        assertEquals(3, view.paragraphs.size)
        assertEquals(2, view.visibleParagraphs.size)
        assertTrue(view.visibleParagraphs.none { it.displayText.contains("filtered") })
    }

    /** A partly filtered paragraph still renders, with the removed text absent. */
    @Test
    fun `a partly filtered paragraph renders without the removed text`() {
        val bookText = "Keep this bit remove that bit keep this too."
        val from = bookText.indexOf("remove")
        val to = bookText.indexOf(" keep this too")

        val view = NarrationReaderState.derive(
            bookText = bookText,
            filteredRanges = listOf(ReaderMask(from, to)),
            narrationTimingRanges = emptyList(),
        )

        val rendered = view.visibleParagraphs.single().displayText
        assertFalse(rendered.contains("remove that bit"))
        assertTrue(rendered.contains("Keep this bit"))
        assertTrue(rendered.contains("keep this too"))
    }

    // endregion

    // region highlighting

    @Test
    fun `the highlight follows the paragraph containing the spoken character`() {
        val bookText = "First paragraph here.\n\nSecond paragraph here.\n\nThird paragraph here."
        val paragraphs = ReaderParagraphParser.parse(bookText)
        val timings = paragraphs.mapIndexed { index, paragraph ->
            ReaderTimingRange(
                startTime = index * 5.0,
                endTime = (index + 1) * 5.0,
                startCharacter = paragraph.startCharacter,
                endCharacter = paragraph.endCharacter,
            )
        }

        val view = NarrationReaderState.derive(
            bookText = bookText,
            filteredRanges = emptyList(),
            narrationTimingRanges = timings,
            bookTimeSeconds = 7.5,
        )

        assertEquals(1, view.highlightedParagraphIndex)
    }

    /**
     * A gap in coverage keeps the previous highlight rather than snapping somewhere.
     *
     * For narration a gap is non-prose or an unrendered chapter, and jumping the highlight
     * to the nearest covered paragraph would show the listener text nobody is reading.
     */
    @Test
    fun `a gap in coverage keeps the previous highlight and does not scroll`() {
        val bookText = "First paragraph here.\n\nSecond paragraph here."
        val paragraphs = ReaderParagraphParser.parse(bookText)
        val timings = listOf(
            ReaderTimingRange(0.0, 5.0, paragraphs[0].startCharacter, paragraphs[0].endCharacter),
        )

        val view = NarrationReaderState.derive(
            bookText = bookText,
            filteredRanges = emptyList(),
            narrationTimingRanges = timings,
            // Past the end of coverage.
            bookTimeSeconds = 30.0,
            previousHighlightIndex = 0,
        )

        assertEquals(0, view.highlightedParagraphIndex)
        assertFalse("a gap must not cause a scroll", view.scrollToHighlight)
    }

    /**
     * Scrolling only when the highlight moves, so a listener who scrolled back to reread
     * something is not dragged forward on every polling tick.
     */
    @Test
    fun `scrolling happens only when the highlight changes`() {
        val bookText = "First paragraph here.\n\nSecond paragraph here."
        val paragraphs = ReaderParagraphParser.parse(bookText)
        val timings = paragraphs.mapIndexed { index, paragraph ->
            ReaderTimingRange(
                index * 5.0,
                (index + 1) * 5.0,
                paragraph.startCharacter,
                paragraph.endCharacter,
            )
        }

        val unchanged = NarrationReaderState.derive(
            bookText = bookText,
            filteredRanges = emptyList(),
            narrationTimingRanges = timings,
            bookTimeSeconds = 2.0,
            previousHighlightIndex = 0,
        )
        val moved = NarrationReaderState.derive(
            bookText = bookText,
            filteredRanges = emptyList(),
            narrationTimingRanges = timings,
            bookTimeSeconds = 7.0,
            previousHighlightIndex = 0,
        )

        assertFalse(unchanged.scrollToHighlight)
        assertTrue(moved.scrollToHighlight)
        assertEquals(1, moved.highlightedParagraphIndex)
    }

    /**
     * Non-prose renders but is never highlighted. It was never spoken, so a highlight there
     * would be pointing at text the voice skipped.
     */
    @Test
    fun `a highlight never lands on a non prose paragraph`() {
        val bookText = "Story paragraph here.\n\nTable of contents entry.\n\nMore story here."
        val paragraphs = ReaderParagraphParser.parse(bookText)
        val nonProse = listOf(SourceRange(paragraphs[1].startCharacter, paragraphs[1].endCharacter))
        // A stray timing pointing into the non-prose paragraph, which is what a bug
        // upstream would produce.
        val timings = listOf(
            ReaderTimingRange(0.0, 5.0, paragraphs[1].startCharacter, paragraphs[1].endCharacter),
        )

        val view = NarrationReaderState.derive(
            bookText = bookText,
            filteredRanges = emptyList(),
            narrationTimingRanges = timings,
            nonProseRanges = nonProse,
            bookTimeSeconds = 2.0,
            previousHighlightIndex = 0,
        )

        assertEquals(0, view.highlightedParagraphIndex)
        // Still rendered, though: the reader shows the whole book.
        assertTrue(view.visibleParagraphs.any { it.displayText.contains("Table of contents") })
    }

    // endregion

    // region tapping

    @Test
    fun `tapping a covered paragraph seeks to its first covered offset`() {
        val bookText = "First paragraph here.\n\nSecond paragraph here."
        val paragraphs = ReaderParagraphParser.parse(bookText)
        val timings = listOf(
            ReaderTimingRange(0.0, 5.0, paragraphs[0].startCharacter, paragraphs[0].endCharacter),
            ReaderTimingRange(5.0, 10.0, paragraphs[1].startCharacter, paragraphs[1].endCharacter),
        )

        val target = NarrationReaderState.tapTarget(paragraphs[1], timings)

        assertTrue(target is TapTarget.Seek)
        assertEquals(5.0, (target as TapTarget.Seek).bookTimeSeconds, 0.001)
    }

    /**
     * A tap on a chapter that has not been produced does nothing and says so. Seeking to
     * zero would throw the listener back to the start of the book.
     */
    @Test
    fun `tapping text with no narration reports it rather than seeking`() {
        val bookText = "Rendered paragraph.\n\nNot yet rendered paragraph."
        val paragraphs = ReaderParagraphParser.parse(bookText)
        val timings = listOf(
            ReaderTimingRange(0.0, 5.0, paragraphs[0].startCharacter, paragraphs[0].endCharacter),
        )

        assertEquals(TapTarget.NoNarrationYet, NarrationReaderState.tapTarget(paragraphs[1], timings))
    }

    @Test
    fun `tapping before anything is rendered reports no narration`() {
        val paragraphs = ReaderParagraphParser.parse("Only paragraph.")

        assertEquals(
            TapTarget.NoNarrationYet,
            NarrationReaderState.tapTarget(paragraphs.single(), emptyList()),
        )
    }

    /**
     * A paragraph beginning with something that was never spoken -- a page number, a
     * footnote marker -- still seeks, to the first offset narration actually covers.
     */
    @Test
    fun `tapping falls forward to the first covered offset in the paragraph`() {
        val bookText = "12 The chapter text begins after the page number."
        val paragraphs = ReaderParagraphParser.parse(bookText)
        val spokenFrom = bookText.indexOf("The chapter")
        val timings = listOf(
            ReaderTimingRange(4.0, 9.0, spokenFrom, bookText.length),
        )

        val target = NarrationReaderState.tapTarget(paragraphs.single(), timings)

        assertTrue(target is TapTarget.Seek)
        assertEquals(4.0, (target as TapTarget.Seek).bookTimeSeconds, 0.001)
    }

    // endregion

    // region reuse

    /**
     * The reader components are used unchanged. If a future change makes narration need its
     * own parser or its own masking, that is a signal the coordinate space diverged, which
     * is the thing this whole design avoids.
     */
    @Test
    fun `paragraphs come from the same parser the audiobook path uses`() {
        val bookText = "One paragraph.\n\nTwo paragraph."

        val view = NarrationReaderState.derive(
            bookText = bookText,
            filteredRanges = emptyList(),
            narrationTimingRanges = emptyList(),
        )

        assertEquals(ReaderParagraphParser.parse(bookText), view.paragraphs)
    }

    /** The substring invariant the reader relies on still holds for narration text. */
    @Test
    fun `paragraphs index book text exactly`(): Unit = runBlocking {
        checkAll(PropTestConfig(iterations = 100), Arb.list(Arb.int(1..4), 1..4)) { counts ->
            val document = documentOf(counts)
            ReaderParagraphParser.parse(document.text).forEach { paragraph ->
                assertEquals(
                    document.text.substring(paragraph.startCharacter, paragraph.endCharacter),
                    paragraph.text,
                )
            }
        }
    }

    // endregion

    // region fixtures

    private companion object {
        val SHA = "e" + "0".repeat(63)
    }

    private fun documentOf(paragraphCounts: List<Int>) = EpubTextReader.readNarrationDocument(
        ByteArrayInputStream(
            epub(
                paragraphCounts.mapIndexed { index, paragraphs ->
                    "chapter$index.xhtml" to buildString {
                        append("<html><body>")
                        (1..paragraphs).forEach {
                            append(
                                "<p>The lantern swung against the rigging and the deck went " +
                                    "silver. Sentence $it followed after it.</p>",
                            )
                        }
                        append("</body></html>")
                    }
                },
            ),
        ),
    )

    private fun epub(spine: List<Pair<String, String>>): ByteArray {
        val manifest = StringBuilder()
        val spineRefs = StringBuilder()
        spine.forEachIndexed { index, (name, _) ->
            manifest.append("""<item id="s$index" href="$name" media-type="application/xhtml+xml"/>""")
            spineRefs.append("""<itemref idref="s$index"/>""")
        }
        val entries = linkedMapOf(
            "META-INF/container.xml" to """
                <?xml version="1.0"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles>
                    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
                  </rootfiles>
                </container>
            """.trimIndent(),
            "OEBPS/content.opf" to """
                <?xml version="1.0"?>
                <package version="3.0" xmlns="http://www.idpf.org/2007/opf">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                    <dc:title>Fixture</dc:title><dc:creator>Author</dc:creator>
                    <dc:language>en</dc:language>
                  </metadata>
                  <manifest>$manifest</manifest>
                  <spine>$spineRefs</spine>
                </package>
            """.trimIndent(),
        )
        spine.forEach { (name, html) -> entries["OEBPS/$name"] = html }
        val output = ByteArrayOutputStream()
        ZipOutputStream(output).use { zip ->
            entries.forEach { (name, contents) ->
                zip.putNextEntry(ZipEntry(name))
                zip.write(contents.toByteArray())
                zip.closeEntry()
            }
        }
        return output.toByteArray()
    }

    // endregion
}
