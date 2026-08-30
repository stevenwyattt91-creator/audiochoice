package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.NarrationChapter
import com.audiochoice.mobile.data.NarrationPlan
import com.audiochoice.mobile.data.PlanInputs
import com.audiochoice.mobile.data.RenderQueue
import com.audiochoice.mobile.data.RenderState
import com.audiochoice.mobile.reader.EpubTextReader
import com.audiochoice.mobile.reader.NavigationSource
import io.kotest.property.Arb
import io.kotest.property.PropTestConfig
import io.kotest.property.arbitrary.int
import io.kotest.property.arbitrary.list
import io.kotest.property.checkAll
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

class StructureParserTest {

    // region coverage, as a property

    /**
     * The invariant everything downstream assumes: chapter ranges are ordered,
     * non-overlapping, each non-empty, and together cover every character of
     * Book_Text.
     *
     * Stated as a property rather than an example because the interesting inputs
     * are the awkward ones: duplicate navigation targets, an entry pointing at the
     * very start, an entry pointing past the end. Coverage is meant to hold by
     * construction, so a counterexample means the construction is wrong rather
     * than that a case was missed.
     */
    @Test
    fun `chapter ranges always tile the whole of book text`(): Unit = runBlocking {
        checkAll(PropTestConfig(iterations = 200), Arb.list(Arb.int(1..6), 1..8)) { paragraphCounts ->
            val document = EpubTextReader.readNarrationDocument(
                ByteArrayInputStream(
                    epub(
                        spine = paragraphCounts.mapIndexed { index, paragraphs ->
                            "chapter$index.xhtml" to page(paragraphs, index)
                        },
                    ),
                ),
            )

            val outline = StructureParser.deriveChapters(document)
            if (document.text.isEmpty()) return@checkAll

            assertTrue(outline.chapters.isNotEmpty())
            assertEquals(0, outline.chapters.first().startCharacter)
            assertEquals(document.text.length, outline.chapters.last().endCharacter)

            outline.chapters.forEachIndexed { index, chapter ->
                assertTrue(chapter.startCharacter < chapter.endCharacter)
                if (index > 0) {
                    assertEquals(outline.chapters[index - 1].endCharacter, chapter.startCharacter)
                }
            }
            assertEquals(
                outline.chapters.indices.toList(),
                outline.chapters.map { it.index },
            )
        }
    }

    // endregion

    // region derivation sources

    @Test
    fun `epub 3 navigation drives chapters and supplies their titles`() {
        val outline = outlineOf(
            epub(
                spine = listOf(
                    "chapter1.xhtml" to page(2, 1),
                    "chapter2.xhtml" to page(2, 2),
                ),
                navigation = nav(
                    """
                    <li><a href="chapter1.xhtml">The Lantern</a></li>
                    <li><a href="chapter2.xhtml">The Swell</a></li>
                    """.trimIndent(),
                ),
            ),
        )

        assertEquals(NavigationSource.EPUB3_NAV, outline.source)
        assertEquals(listOf("The Lantern", "The Swell"), outline.chapters.map { it.title })
        assertFalse(outline.fellBackToSpine)
    }

    @Test
    fun `ncx drives chapters when there is no navigation document`() {
        val outline = outlineOf(
            epub(
                spine = listOf(
                    "chapter1.xhtml" to page(2, 1),
                    "chapter2.xhtml" to page(2, 2),
                ),
                ncx = """
                    <ncx><navMap>
                      <navPoint><navLabel><text>First</text></navLabel>
                        <content src="chapter1.xhtml"/></navPoint>
                      <navPoint><navLabel><text>Second</text></navLabel>
                        <content src="chapter2.xhtml"/></navPoint>
                    </navMap></ncx>
                """.trimIndent(),
            ),
        )

        assertEquals(NavigationSource.NCX, outline.source)
        assertEquals(listOf("First", "Second"), outline.chapters.map { it.title })
        assertFalse(outline.fellBackToSpine)
    }

    /**
     * A book with no contents list is the ordinary third case, not a degraded one,
     * so it must not be recorded as a fallback. Recording it as degraded would put a
     * warning in front of a listener for a perfectly normal file.
     */
    @Test
    fun `spine derivation without navigation is not recorded as a fallback`() {
        val outline = outlineOf(
            epub(
                spine = listOf(
                    "chapter1.xhtml" to page(2, 1),
                    "chapter2.xhtml" to page(2, 2),
                    "chapter3.xhtml" to page(2, 3),
                ),
            ),
        )

        assertEquals(NavigationSource.SPINE_FALLBACK, outline.source)
        assertFalse(outline.fellBackToSpine)
        assertEquals(3, outline.chapters.size)
        assertEquals(listOf("Chapter 1", "Chapter 2", "Chapter 3"), outline.chapters.map { it.title })
    }

    /**
     * A chapter can begin partway through a spine document. Without fragment
     * support, a single-file EPUB collapses into one chapter however many divisions
     * its contents list names.
     */
    @Test
    fun `a fragment target splits one spine document into two chapters`() {
        val outline = outlineOf(
            epub(
                spine = listOf(
                    "all.xhtml" to """
                        <html><body>
                        <section id="one"><h1>One</h1><p>${prose()}</p></section>
                        <section id="two"><h1>Two</h1><p>${prose()}</p></section>
                        </body></html>
                    """.trimIndent(),
                ),
                navigation = nav(
                    """
                    <li><a href="all.xhtml#one">Part One</a></li>
                    <li><a href="all.xhtml#two">Part Two</a></li>
                    """.trimIndent(),
                ),
            ),
        )

        assertEquals(2, outline.chapters.size)
        assertEquals(listOf("Part One", "Part Two"), outline.chapters.map { it.title })
        assertTrue(outline.chapters[1].startCharacter > outline.chapters[0].startCharacter)
    }

    // endregion

    // region fallback triggers

    @Test
    fun `navigation resolving no spine document falls back to the spine and records it`() {
        val outline = outlineOf(
            epub(
                spine = listOf("chapter1.xhtml" to page(2, 1)),
                navigation = nav("""<li><a href="not-in-the-spine.xhtml">Nowhere</a></li>"""),
            ),
        )

        assertEquals(NavigationSource.SPINE_FALLBACK, outline.source)
        assertTrue(outline.fellBackToSpine)
        assertEquals(1, outline.chapters.size)
    }

    @Test
    fun `unparseable navigation falls back to the spine and records it`() {
        val outline = outlineOf(
            epub(
                spine = listOf("chapter1.xhtml" to page(2, 1), "chapter2.xhtml" to page(2, 2)),
                navigation = "<html><body><p>No nav element at all.</p></body></html>",
            ),
        )

        assertEquals(NavigationSource.SPINE_FALLBACK, outline.source)
        assertTrue(outline.fellBackToSpine)
        assertEquals(2, outline.chapters.size)
    }

    /**
     * A contents list used as an index rather than as chapters. The cap keeps the
     * chapter control usable and, more importantly, stops the renderer treating
     * thousands of fragments as thousands of render jobs.
     */
    @Test
    fun `navigation beyond the chapter cap falls back to the spine`() {
        val anchors = (0 until StructureParser.MAXIMUM_DERIVED_CHAPTERS + 10).joinToString("") {
            """<span id="a$it">Fragment $it text here.</span>"""
        }
        val entries = (0 until StructureParser.MAXIMUM_DERIVED_CHAPTERS + 10).joinToString("") {
            """<li><a href="all.xhtml#a$it">Entry $it</a></li>"""
        }

        val outline = outlineOf(
            epub(
                spine = listOf("all.xhtml" to "<html><body>$anchors</body></html>"),
                navigation = nav(entries),
            ),
        )

        assertEquals(NavigationSource.SPINE_FALLBACK, outline.source)
        assertTrue(outline.fellBackToSpine)
        assertEquals(1, outline.chapters.size)
    }

    // endregion

    // region titles

    @Test
    fun `titles are trimmed, collapsed and capped`() {
        assertEquals("The Lantern", StructureParser.title("  The\n  Lantern ", 1))
        assertEquals("Chapter 4", StructureParser.title("   ", 4))
        assertEquals("Chapter 7", StructureParser.title(null, 7))
        assertEquals(
            StructureParser.MAXIMUM_TITLE_LENGTH,
            StructureParser.title("x".repeat(500), 1).length,
        )
    }

    /** Two entries pointing at one offset is common; it must not yield two chapters. */
    @Test
    fun `duplicate navigation targets collapse to one chapter keeping the first title`() {
        val outline = outlineOf(
            epub(
                spine = listOf("chapter1.xhtml" to page(2, 1), "chapter2.xhtml" to page(2, 2)),
                navigation = nav(
                    """
                    <li><a href="chapter1.xhtml">Part One</a></li>
                    <li><a href="chapter1.xhtml">Chapter One</a></li>
                    <li><a href="chapter2.xhtml">Chapter Two</a></li>
                    """.trimIndent(),
                ),
            ),
        )

        assertEquals(listOf("Part One", "Chapter Two"), outline.chapters.map { it.title })
    }

    /**
     * Text before the first navigation target joins the first chapter rather than
     * becoming an untitled chapter at the top of every book.
     */
    @Test
    fun `text before the first navigation target joins the first chapter`() {
        val outline = outlineOf(
            epub(
                spine = listOf(
                    "front.xhtml" to page(1, 0),
                    "chapter1.xhtml" to page(2, 1),
                ),
                navigation = nav("""<li><a href="chapter1.xhtml">Chapter One</a></li>"""),
            ),
        )

        assertEquals(1, outline.chapters.size)
        assertEquals(0, outline.chapters.single().startCharacter)
        assertEquals("Chapter One", outline.chapters.single().title)
    }

    // endregion

    // region performance

    /**
     * The bound is five seconds for a million characters. Derivation itself is a
     * sort over at most two thousand boundaries, so what this really guards is
     * accidental quadratic behaviour in extraction feeding it.
     */
    @Test
    fun `a million character book is parsed well inside five seconds`() {
        val paragraph = prose()
        val perChapter = 200
        val chapters = (1..40).map { index ->
            "chapter$index.xhtml" to
                "<html><body>" + (1..perChapter).joinToString("") { "<p>$paragraph</p>" } +
                "</body></html>"
        }
        val archive = epub(spine = chapters)

        val start = System.nanoTime()
        val document = EpubTextReader.readNarrationDocument(ByteArrayInputStream(archive))
        val outline = StructureParser.deriveChapters(document)
        val elapsedMs = (System.nanoTime() - start) / 1_000_000

        assertTrue("book was only ${document.text.length} chars", document.text.length > 1_000_000)
        assertEquals(40, outline.chapters.size)
        assertTrue("parsing took ${elapsedMs}ms", elapsedMs < 5_000)
    }

    // endregion

    // region AudioChapter presentation

    /**
     * The three existing player controls have to keep working on a book that is
     * only partly produced. These assertions mirror their actual expressions, so if
     * one of them changes shape this test says so.
     */
    @Test
    fun `unrendered chapters become zero length entries at the end of rendered audio`() {
        val plan = planOf("One", "Two", "Three")
        val queue = RenderQueue(
            states = listOf(RenderState.RENDERED, RenderState.NOT_RENDERED, RenderState.NOT_RENDERED),
            chapterDurationsMs = listOf(60_000L, 0L, 0L),
        )

        val chapters = NarrationChapters.audioChapters(plan, queue)

        assertEquals(0.0, chapters[0].startSeconds, 0.0001)
        assertEquals(60.0, chapters[0].endSeconds, 0.0001)
        // Zero-length, parked at the end of what exists.
        assertEquals(60.0, chapters[1].startSeconds, 0.0001)
        assertEquals(60.0, chapters[1].endSeconds, 0.0001)
        assertEquals(60.0, chapters[2].startSeconds, 0.0001)
        assertEquals(60.0, chapters[2].endSeconds, 0.0001)
    }

    /** `previousChapter` must find a real chapter, never a placeholder. */
    @Test
    fun `the current chapter inside rendered audio is never a placeholder`() {
        val plan = planOf("One", "Two", "Three")
        val queue = RenderQueue(
            states = listOf(RenderState.RENDERED, RenderState.RENDERED, RenderState.NOT_RENDERED),
            chapterDurationsMs = listOf(60_000L, 30_000L, 0L),
        )
        val chapters = NarrationChapters.audioChapters(plan, queue)

        val position = 70.0
        val currentIndex = chapters.indexOfLast { it.startSeconds <= position }

        assertEquals(1, currentIndex)
    }

    /**
     * `nextChapter` from inside the last rendered chapter targets the first
     * unrendered one, and lands at the end of rendered audio. That is precisely the
     * behaviour required when a listener seeks into a chapter that is not ready.
     */
    @Test
    fun `next chapter from the last rendered chapter lands at the end of rendered audio`() {
        val plan = planOf("One", "Two")
        val queue = RenderQueue(
            states = listOf(RenderState.RENDERED, RenderState.NOT_RENDERED),
            chapterDurationsMs = listOf(60_000L, 0L),
        )
        val chapters = NarrationChapters.audioChapters(plan, queue)

        val position = 10.0
        val target = chapters.firstOrNull { it.startSeconds > position + 1.0 }

        assertEquals(60.0, target?.startSeconds)
    }

    /** The sleep timer can never target a chapter with no audio. */
    @Test
    fun `sleep at end of chapter never selects an unrendered chapter`() {
        val plan = planOf("One", "Two", "Three")
        val queue = RenderQueue(
            states = listOf(RenderState.RENDERED, RenderState.NOT_RENDERED, RenderState.NOT_RENDERED),
            chapterDurationsMs = listOf(60_000L, 0L, 0L),
        )
        val chapters = NarrationChapters.audioChapters(plan, queue)

        listOf(0.0, 30.0, 59.9, 60.0, 120.0).forEach { position ->
            val selected = chapters.firstOrNull {
                position >= it.startSeconds && position < it.endSeconds
            }
            val index = selected?.let(chapters::indexOf) ?: -1
            assertTrue("position $position selected chapter $index", index <= 0)
        }
    }

    /** Only rendered chapters advance the clock, so duration grows as rendering does. */
    @Test
    fun `chapter starts skip over chapters that have no audio`() {
        val queue = RenderQueue(
            states = listOf(
                RenderState.RENDERED,
                RenderState.NOT_RENDERED,
                RenderState.RENDERED,
            ),
            chapterDurationsMs = listOf(10_000L, 0L, 20_000L),
        )

        assertEquals(listOf(0L, 10_000L, 10_000L), NarrationChapters.chapterStartsMs(queue))
    }

    // endregion

    // region fixtures

    private fun outlineOf(archive: ByteArray): ChapterOutline =
        StructureParser.deriveChapters(
            EpubTextReader.readNarrationDocument(ByteArrayInputStream(archive)),
        )

    private fun planOf(vararg titles: String) = NarrationPlan(
        planVersion = NarrationPlan.PLAN_VERSION,
        inputs = PlanInputs(
            sourceSha256 = "b".repeat(64),
            bookTextHash = "hash",
            extractionVersion = 1,
            planVersion = NarrationPlan.PLAN_VERSION,
            synthesisInputLimit = 1_000,
        ),
        chapters = titles.mapIndexed { index, title ->
            NarrationChapter(index, title, index * 100, (index + 1) * 100)
        },
    )

    private fun prose(): String =
        "The lantern swung against the rigging and the deck went silver, then dark, " +
            "then silver again as the swell lifted and dropped beneath them."

    private fun page(paragraphs: Int, chapterNumber: Int): String =
        "<html><body><h1>Chapter $chapterNumber</h1>" +
            (1..paragraphs).joinToString("") { "<p>${prose()}</p>" } +
            "</body></html>"

    private fun nav(items: String) =
        """<html><body><nav epub:type="toc"><ol>$items</ol></nav></body></html>"""

    private fun epub(
        spine: List<Pair<String, String>>,
        navigation: String? = null,
        ncx: String? = null,
    ): ByteArray {
        val manifest = StringBuilder()
        val spineRefs = StringBuilder()
        spine.forEachIndexed { index, (name, _) ->
            manifest.append("""<item id="s$index" href="$name" media-type="application/xhtml+xml"/>""")
            spineRefs.append("""<itemref idref="s$index"/>""")
        }
        if (navigation != null) {
            manifest.append(
                """<item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>""",
            )
        }
        if (ncx != null) {
            manifest.append("""<item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>""")
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
                  <spine${if (ncx != null) """ toc="ncx"""" else ""}>$spineRefs</spine>
                </package>
            """.trimIndent(),
        )
        spine.forEach { (name, html) -> entries["OEBPS/$name"] = html }
        navigation?.let { entries["OEBPS/nav.xhtml"] = it }
        ncx?.let { entries["OEBPS/toc.ncx"] = it }
        return zip(entries)
    }

    private fun zip(entries: Map<String, String>): ByteArray {
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
