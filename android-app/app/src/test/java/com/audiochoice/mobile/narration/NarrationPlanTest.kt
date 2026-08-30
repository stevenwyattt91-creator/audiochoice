package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.SourceRange
import com.audiochoice.mobile.reader.EpubDocument
import com.audiochoice.mobile.reader.EpubTextReader
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

/**
 * The plan is where character offsets are decided, and every later feature trusts
 * them: the reader highlights by offset, filters remove by offset, the timeline
 * converts by offset. So the invariants here are stated as properties over
 * generated books rather than as examples, because an example only proves the case
 * someone thought of.
 */
class NarrationPlanTest {

    private val limit = SynthesisInputLimit.CEILING

    // region the invariants, as properties

    /**
     * The one that matters most: a unit's recorded characters are exactly the
     * characters at its recorded offsets.
     *
     * If this ever drifts, a filter removes the wrong passage and a highlight lands
     * on the wrong sentence, and neither failure is visible from the code.
     */
    @Test
    fun `every unit indexes book text exactly`(): Unit = runBlocking {
        checkAll(PropTestConfig(iterations = 150), bookShapes()) { shape ->
            val document = documentOf(shape)
            val plan = StructureParser.buildPlan(document, SHA, limit) ?: return@checkAll

            plan.chapters.forEach { chapter ->
                chapter.units.forEach { unit ->
                    assertEquals(
                        document.text.substring(unit.startCharacter, unit.endCharacter),
                        unit.sourceCharacters,
                    )
                }
            }
        }
    }

    @Test
    fun `units are ordered, non empty, inside their chapter and never overlapping`(): Unit =
        runBlocking {
            checkAll(PropTestConfig(iterations = 150), bookShapes()) { shape ->
                val document = documentOf(shape)
                val plan = StructureParser.buildPlan(document, SHA, limit) ?: return@checkAll

                plan.chapters.forEach { chapter ->
                    chapter.units.forEachIndexed { index, unit ->
                        assertTrue(unit.startCharacter < unit.endCharacter)
                        assertTrue(unit.startCharacter >= chapter.startCharacter)
                        assertTrue(unit.endCharacter <= chapter.endCharacter)
                        val next = chapter.units.getOrNull(index + 1) ?: return@forEachIndexed
                        assertTrue(
                            "unit $index ended at ${unit.endCharacter}, next began at " +
                                "${next.startCharacter}",
                            unit.endCharacter <= next.startCharacter,
                        )
                    }
                }
            }
        }

    /**
     * A unit may not touch a non-prose region by even one character. Segmenting over
     * prose sub-ranges is what makes this unconstructible, so a counterexample means
     * the subtraction is wrong rather than that a case was missed.
     */
    @Test
    fun `no unit overlaps a non prose region`(): Unit = runBlocking {
        checkAll(PropTestConfig(iterations = 150), bookShapes()) { shape ->
            val document = documentOf(shape, includeTable = true)
            val plan = StructureParser.buildPlan(document, SHA, limit) ?: return@checkAll

            plan.chapters.forEach { chapter ->
                chapter.units.forEach { unit ->
                    val unitRange = SourceRange(unit.startCharacter, unit.endCharacter)
                    document.nonProseRanges.forEach { blocked ->
                        assertFalse(
                            "unit ${unit.sourceCharacters.take(30)} overlaps non-prose",
                            unitRange.overlaps(blocked),
                        )
                    }
                }
            }
        }
    }

    /** Nothing is queued for synthesis that has nothing to say. */
    @Test
    fun `every unit holds a letter or digit`(): Unit = runBlocking {
        checkAll(PropTestConfig(iterations = 150), bookShapes()) { shape ->
            val document = documentOf(shape, includeSceneBreaks = true)
            val plan = StructureParser.buildPlan(document, SHA, limit) ?: return@checkAll

            plan.chapters.forEach { chapter ->
                chapter.units.forEach { unit ->
                    assertTrue(
                        "unit was ${unit.sourceCharacters.take(30)}",
                        unit.sourceCharacters.any { it.isLetterOrDigit() },
                    )
                }
            }
        }
    }

    /**
     * The same book must plan identically on every run. A plan that differed between
     * runs would change its Book_Text hash and look like a changed book, discarding
     * rendered audio for no reason.
     */
    @Test
    fun `the same book and inputs always produce an equal plan`(): Unit = runBlocking {
        checkAll(PropTestConfig(iterations = 80), bookShapes()) { shape ->
            val document = documentOf(shape)
            val first = StructureParser.buildPlan(document, SHA, limit)
            val second = StructureParser.buildPlan(document, SHA, limit)
            val third = StructureParser.buildPlan(documentOf(shape), SHA, limit)

            assertEquals(first, second)
            assertEquals(first, third)
        }
    }

    /** No unit may exceed the engine's input limit, whatever the prose looks like. */
    @Test
    fun `no unit exceeds the synthesis input limit`(): Unit = runBlocking {
        checkAll(PropTestConfig(iterations = 100), Arb.int(60..400)) { smallLimit ->
            val document = documentOf(listOf(3, 2))
            val plan = StructureParser.buildPlan(document, SHA, smallLimit) ?: return@checkAll

            plan.chapters.forEach { chapter ->
                chapter.units.forEach { unit ->
                    assertTrue(
                        "unit was ${unit.length} chars against a limit of $smallLimit",
                        unit.length <= smallLimit,
                    )
                }
            }
        }
    }

    // endregion

    // region segmentation behaviour

    /**
     * Sentences are the unit a listener perceives, so an ordinary sentence must
     * survive intact. The abbreviation and the decimal are the two cases a
     * punctuation regex always gets wrong.
     */
    @Test
    fun `sentences are not split at abbreviations or decimals`() {
        val text = "Mr. Darcy walked 1.5 miles to the house. Then he turned back."
        val units = UnitSegmenter.segment(
            bookText = text,
            chapterRange = SourceRange(0, text.length),
            nonProseRanges = emptyList(),
            limit = limit,
            language = "en",
        )

        assertEquals(2, units.size)
        assertEquals("Mr. Darcy walked 1.5 miles to the house.", units[0].sourceCharacters)
        assertEquals("Then he turned back.", units[1].sourceCharacters)
    }

    /** A long sentence splits at clauses, and the pieces still index the text. */
    @Test
    fun `an over long sentence splits at clause boundaries`() {
        val clause = "the lantern swung against the rigging and the deck went silver, "
        val text = "In the dark, " + clause.repeat(6) + "and then it was morning."
        val units = UnitSegmenter.segment(
            bookText = text,
            chapterRange = SourceRange(0, text.length),
            nonProseRanges = emptyList(),
            limit = 120,
            language = "en",
        )

        assertTrue(units.size > 1)
        units.forEach { unit ->
            assertTrue(unit.length <= 120)
            assertEquals(
                text.substring(unit.startCharacter, unit.endCharacter),
                unit.sourceCharacters,
            )
        }
    }

    /** A clause with no punctuation falls through to word boundaries. */
    @Test
    fun `a long unpunctuated run splits at word boundaries`() {
        val text = "word ".repeat(80).trim() + "."
        val units = UnitSegmenter.segment(
            bookText = text,
            chapterRange = SourceRange(0, text.length),
            nonProseRanges = emptyList(),
            limit = 60,
            language = "en",
        )

        assertTrue(units.size > 1)
        units.forEach { unit ->
            assertTrue(unit.length <= 60)
            // Split at a boundary, so no unit begins or ends mid-word.
            assertFalse(unit.sourceCharacters.startsWith(" "))
            assertFalse(unit.sourceCharacters.endsWith(" "))
        }
    }

    /**
     * A single token longer than the limit has no boundary to cut at. Cutting
     * mid-token is the right trade: the alternative is a request the engine rejects
     * and a chapter that never renders.
     */
    @Test
    fun `a single token longer than the limit is still emitted within the limit`() {
        val text = "a".repeat(200)
        val units = UnitSegmenter.segment(
            bookText = text,
            chapterRange = SourceRange(0, text.length),
            nonProseRanges = emptyList(),
            limit = 50,
            language = "en",
        )

        assertTrue(units.isNotEmpty())
        units.forEach { assertTrue(it.length <= 50) }
        assertEquals(200, units.sumOf { it.length })
    }

    /** Units carry no leading or trailing whitespace, by moving offsets not copying. */
    @Test
    fun `units are trimmed by narrowing their offsets`() {
        val text = "   Leading space.   Trailing space.   "
        val units = UnitSegmenter.segment(
            bookText = text,
            chapterRange = SourceRange(0, text.length),
            nonProseRanges = emptyList(),
            limit = limit,
            language = "en",
        )

        units.forEach { unit ->
            assertEquals(unit.sourceCharacters.trim(), unit.sourceCharacters)
            assertEquals(
                text.substring(unit.startCharacter, unit.endCharacter),
                unit.sourceCharacters,
            )
        }
    }

    /** A scene break of asterisks has nothing to say and must not be queued. */
    @Test
    fun `punctuation only spans produce no unit`() {
        val text = "* * *"
        val units = UnitSegmenter.segment(
            bookText = text,
            chapterRange = SourceRange(0, text.length),
            nonProseRanges = emptyList(),
            limit = limit,
            language = "en",
        )

        assertTrue(units.isEmpty())
    }

    // endregion

    // region prose subtraction

    @Test
    fun `subtraction removes non prose and keeps the gaps between`() {
        val result = UnitSegmenter.subtract(
            SourceRange(0, 100),
            listOf(SourceRange(10, 20), SourceRange(50, 60)),
        )

        assertEquals(
            listOf(SourceRange(0, 10), SourceRange(20, 50), SourceRange(60, 100)),
            result,
        )
    }

    @Test
    fun `subtraction of a fully covering region leaves nothing`() {
        assertTrue(
            UnitSegmenter.subtract(SourceRange(10, 40), listOf(SourceRange(0, 100))).isEmpty(),
        )
    }

    @Test
    fun `subtraction ignores regions outside the range`() {
        assertEquals(
            listOf(SourceRange(10, 40)),
            UnitSegmenter.subtract(
                SourceRange(10, 40),
                listOf(SourceRange(0, 5), SourceRange(80, 90)),
            ),
        )
    }

    // endregion

    // region empty chapters and empty plans

    /**
     * A chapter of pure front matter is kept so the chapter control still shows the
     * division the author named, but marked as needing no rendering. Dropping it
     * would renumber every chapter after it.
     */
    @Test
    fun `a chapter with no prose is kept and marked as needing no rendering`() {
        val document = documentOf(
            spine = listOf(
                "front.xhtml" to
                    """<html><body epub:type="titlepage"><p>A Novel</p></body></html>""",
                "chapter1.xhtml" to page(3),
            ),
            navigationItems = """
                <li><a href="front.xhtml">Title Page</a></li>
                <li><a href="chapter1.xhtml">Chapter One</a></li>
            """.trimIndent(),
        )

        val plan = StructureParser.buildPlan(document, SHA, limit)
        assertNotNull(plan)
        assertEquals(2, plan!!.chapters.size)
        assertFalse(plan.chapters[0].requiresRendering)
        assertTrue(plan.chapters[1].requiresRendering)
    }

    /**
     * A book that yields no unit at all gets no plan. Persisting one would leave an
     * entry in the library that can never play and never explain why.
     */
    @Test
    fun `a book with no narratable prose yields no plan`() {
        val document = documentOf(
            spine = listOf(
                "cover.xhtml" to
                    """<html><body epub:type="cover"><p>Cover image caption</p></body></html>""",
            ),
        )

        assertNull(StructureParser.buildPlan(document, SHA, limit))
    }

    @Test
    fun `plan inputs record everything a stale check needs`() {
        val document = documentOf(listOf(3, 2))
        val plan = StructureParser.buildPlan(
            document = document,
            sourceSha256 = SHA.uppercase(),
            synthesisInputLimit = 640,
            enabledEventKeys = listOf("violence", "profanity"),
            pronunciationRuleFingerprint = "rules-v3",
        )!!

        assertEquals(SHA, plan.inputs.sourceSha256)
        assertEquals(640, plan.inputs.synthesisInputLimit)
        assertEquals(document.extractionVersion, plan.inputs.extractionVersion)
        // Sorted, so an unordered set of enabled keys cannot make two identical
        // plans compare unequal.
        assertEquals(listOf("profanity", "violence"), plan.inputs.enabledEventKeys)
        assertEquals("rules-v3", plan.inputs.pronunciationRuleFingerprint)
        assertEquals(
            NarrationStore.bookTextHash(document.text, document.extractionVersion),
            plan.inputs.bookTextHash,
        )
    }

    // endregion

    // region input limit resolution

    @Test
    fun `input limit is capped so the plan does not depend on the selected voice`() {
        assertEquals(SynthesisInputLimit.CEILING, SynthesisInputLimit.resolve(4_000))
        assertEquals(600, SynthesisInputLimit.resolve(600))
        // A platform reporting nothing usable falls back to the ceiling rather than
        // producing a plan of one-character units.
        assertEquals(SynthesisInputLimit.CEILING, SynthesisInputLimit.resolve(0))
        assertEquals(SynthesisInputLimit.FLOOR, SynthesisInputLimit.resolve(3))
    }

    // endregion

    // region fixtures

    private companion object {
        val SHA = "c" + "0".repeat(63)
    }

    private fun bookShapes(): Arb<List<Int>> = Arb.list(Arb.int(0..4), 1..5)

    private fun prose(seed: Int): String =
        "The lantern swung against the rigging and the deck went silver, then dark, " +
            "then silver again as the swell lifted beneath them. Sentence $seed followed, " +
            "and the watch changed; nobody spoke of it afterwards."

    private fun page(paragraphs: Int): String =
        "<html><body>" + (1..paragraphs).joinToString("") { "<p>${prose(it)}</p>" } + "</body></html>"

    private fun documentOf(
        paragraphCounts: List<Int>,
        includeTable: Boolean = false,
        includeSceneBreaks: Boolean = false,
    ): EpubDocument = documentOf(
        spine = paragraphCounts.mapIndexed { index, paragraphs ->
            val body = buildString {
                append("<html><body>")
                (1..paragraphs).forEach { append("<p>${prose(it)}</p>") }
                if (includeTable) append("<table><tr><td>Row ${index}A</td><td>Row ${index}B</td></tr></table>")
                if (includeSceneBreaks) append("<p>* * *</p><p>&mdash;</p>")
                append("</body></html>")
            }
            "chapter$index.xhtml" to body
        },
    )

    private fun documentOf(
        spine: List<Pair<String, String>>,
        navigationItems: String? = null,
    ): EpubDocument = EpubTextReader.readNarrationDocument(
        ByteArrayInputStream(epub(spine, navigationItems)),
    )

    private fun epub(spine: List<Pair<String, String>>, navigationItems: String?): ByteArray {
        val manifest = StringBuilder()
        val spineRefs = StringBuilder()
        spine.forEachIndexed { index, (name, _) ->
            manifest.append("""<item id="s$index" href="$name" media-type="application/xhtml+xml"/>""")
            spineRefs.append("""<itemref idref="s$index"/>""")
        }
        if (navigationItems != null) {
            manifest.append(
                """<item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>""",
            )
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
                    <dc:title>Fixture</dc:title>
                    <dc:creator>Author</dc:creator>
                    <dc:language>en</dc:language>
                  </metadata>
                  <manifest>$manifest</manifest>
                  <spine>$spineRefs</spine>
                </package>
            """.trimIndent(),
        )
        spine.forEach { (name, html) -> entries["OEBPS/$name"] = html }
        navigationItems?.let {
            entries["OEBPS/nav.xhtml"] =
                """<html><body><nav epub:type="toc"><ol>$it</ol></nav></body></html>"""
        }

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
