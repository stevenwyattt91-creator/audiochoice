package com.audiochoice.mobile.narration

import com.audiochoice.mobile.reader.EpubTextReader
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayOutputStream
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

/**
 * Two faults found by narrating a real 1.5-million-character novel.
 *
 * Both were invisible to synthetic fixtures because both need a book whose contents list names Parts
 * at the top level and chapters beneath them, which is an extremely ordinary shape and was not among
 * the fixtures.
 *
 * **Parts were treated as chapters.** The navigation parser deliberately took top-level entries only,
 * to stop a book that lists every scene break producing hundreds of chapters. For that book the top
 * level was six Parts, so its first "chapter" held 440,988 spoken characters -- around seven hours of
 * audio and a quarter of an hour of synthesis, all of which has to finish before a word can be heard.
 * The listener saw a button that appeared to do nothing.
 *
 * **Front matter was narrated.** Two title pages, a dedication, an epigraph and a full table of
 * contents came before the first sentence, so the voice opened with several minutes of matter nobody
 * asked to hear.
 */
class PartsAndFrontMatterTest {

    /**
     * A Part is broken into the chapters inside it.
     *
     * Size is the test, not nesting depth: a chapter is what must be synthesised in full before any
     * of it plays, so how large it is bounds how long the listener waits.
     */
    @Test
    fun `a part larger than a chapter is replaced by the chapters inside it`() {
        val plan = planFor(
            partBody = "Part One opens here.",
            chapterBodies = listOf(prose(30_000), prose(30_000), prose(30_000)),
        )
        val titles = plan.chapters.mapNotNull { it.title }
        assertTrue(
            "the Part was kept as a single chapter, so the render unit is the whole Part: $titles",
            titles.containsAll(listOf("One", "Two", "Three")),
        )
        val largest = plan.chapters.maxOf { chapter ->
            chapter.units.sumOf { it.sourceCharacters.length }
        }
        assertTrue(
            "a chapter of $largest characters survived, past the ${
                StructureParser.MAXIMUM_CHAPTER_CHARACTERS
            } limit that bounds how long a listener waits before hearing anything",
            largest <= StructureParser.MAXIMUM_CHAPTER_CHARACTERS,
        )
    }

    /**
     * The Part's own text survives the split.
     *
     * A Part title, or an epigraph opening it, sits before the first chapter begins. Replacing the
     * Part outright with its children would leave that text in no chapter at all, so it would never
     * be spoken and never be reachable.
     */
    @Test
    fun `the part keeps its own opening text as its first division`() {
        val plan = planFor(
            partBody = "Book One The Briars 1989",
            chapterBodies = listOf(prose(30_000), prose(30_000), prose(30_000)),
        )
        val part = plan.chapters.first { it.title == "Part One" }
        val spoken = part.units.joinToString(" ") { it.sourceCharacters }
        assertTrue(
            "the Part's own opening text is no longer spoken by any chapter: '$spoken'",
            spoken.contains("The Briars"),
        )
    }

    /**
     * A contents list already made of chapters is left exactly as the author named it.
     *
     * This is what the top-level-only rule was protecting, and it is worth keeping: descending into
     * every scene break would put hundreds of rows in the chapter control and hundreds of render jobs
     * behind them.
     */
    @Test
    fun `chapter-sized top level entries are not descended into`() {
        val plan = planFor(
            partBody = "Part One opens here.",
            chapterBodies = listOf(prose(400), prose(400), prose(400)),
        )
        assertEquals(
            "a Part small enough to be a chapter was split anyway",
            listOf("Part One"),
            plan.chapters.mapNotNull { it.title },
        )
    }

    /**
     * Everything before the book's own declared body start is silent.
     *
     * The declaration is the book's, not a guess from filenames: EPUB 2 names it as the guide
     * reference of type "text", EPUB 3 as the landmark of type "bodymatter".
     */
    @Test
    fun `front matter before the declared body start is not narrated`() {
        val plan = planFor(
            partBody = "Book One begins.",
            chapterBodies = listOf(prose(30_000), prose(30_000), prose(30_000)),
            frontMatter = listOf(
                "titlepage.xhtml" to "<p>WatermarkSite.com</p>",
                "dedication.xhtml" to "<p>For Gillian, a thousand times</p>",
            ),
        )
        val frontMatterChapters = plan.chapters.filter {
            it.title == "Cover" || it.title == "Dedication"
        }
        assertTrue("the front matter chapters are missing entirely", frontMatterChapters.size == 2)
        frontMatterChapters.forEach { chapter ->
            assertEquals(
                "'${chapter.title}' is still narrated, so the voice opens with it instead of the " +
                    "book",
                0,
                chapter.units.sumOf { it.sourceCharacters.length },
            )
        }
        // And the story itself is still spoken.
        assertTrue(
            "silencing the front matter also silenced the book",
            plan.chapters.sumOf { c -> c.units.sumOf { it.sourceCharacters.length } } > 50_000,
        )
    }

    /** A book declaring its first document as the body start has no front matter to silence. */
    @Test
    fun `a body start at the first document silences nothing`() {
        val plan = planFor(
            partBody = "Book One begins.",
            chapterBodies = listOf(prose(30_000)),
            frontMatter = emptyList(),
        )
        assertTrue(
            "the book was silenced despite declaring no front matter",
            plan.chapters.sumOf { c -> c.units.sumOf { it.sourceCharacters.length } } > 10_000,
        )
    }

    // region fixtures

    private fun prose(approximateCharacters: Int): String {
        val sentence = "The lake lay still under a sky the colour of slate that morning. "
        val builder = StringBuilder()
        while (builder.length < approximateCharacters) builder.append(sentence)
        return "<p>$builder</p>"
    }

    private fun planFor(
        partBody: String,
        chapterBodies: List<String>,
        frontMatter: List<Pair<String, String>> = emptyList(),
    ): com.audiochoice.mobile.data.NarrationPlan {
        val bytes = epubWithParts(partBody, chapterBodies, frontMatter)
        val document = bytes.inputStream().use { EpubTextReader.readNarrationDocument(it) }
        return requireNotNull(
            StructureParser.buildPlan(
                document = document,
                sourceSha256 = "a".repeat(64),
                synthesisInputLimit = 3_000,
            ),
        ) { "the fixture produced no plan at all" }
    }

    /**
     * A book shaped like the one that exposed both faults: front matter, then a Part whose chapters
     * are nested beneath it in the contents list.
     */
    private fun epubWithParts(
        partBody: String,
        chapterBodies: List<String>,
        frontMatter: List<Pair<String, String>>,
    ): ByteArray {
        val documents = linkedMapOf<String, String>()
        frontMatter.forEach { (name, body) -> documents[name] = page(body) }
        documents["nav.xhtml"] = ""
        documents["part_1.xhtml"] = page("<h1 id=\"p1\">$partBody</h1>")
        chapterBodies.forEachIndexed { index, body ->
            documents["chapter_${index + 1}.xhtml"] = page("<h2 id=\"c$index\">Chapter</h2>$body")
        }

        val manifest = StringBuilder()
        val spineRefs = StringBuilder()
        documents.keys.forEachIndexed { index, name ->
            val properties = if (name == "nav.xhtml") """ properties="nav"""" else ""
            manifest.append(
                """<item id="s$index" href="$name" media-type="application/xhtml+xml"$properties/>""",
            )
            spineRefs.append("""<itemref idref="s$index"/>""")
        }

        val names = listOf("One", "Two", "Three", "Four", "Five")
        val nestedItems = chapterBodies.indices.joinToString("") { index ->
            """<li><a href="chapter_${index + 1}.xhtml#c$index">${names[index]}</a></li>"""
        }
        val frontMatterItems = frontMatter.joinToString("") { (name, _) ->
            val label = if (name.contains("dedication")) "Dedication" else "Cover"
            """<li><a href="$name">$label</a></li>"""
        }
        // The landmark is what declares where the body begins.
        documents["nav.xhtml"] = page(
            """
            <nav epub:type="toc"><ol>
              $frontMatterItems
              <li><a href="part_1.xhtml#p1">Part One</a><ol>$nestedItems</ol></li>
            </ol></nav>
            <nav epub:type="landmarks" hidden="hidden"><ol>
              <li><a epub:type="bodymatter" href="part_1.xhtml#p1">Part One</a></li>
            </ol></nav>
            """.trimIndent(),
        )

        val entries = linkedMapOf(
            "META-INF/container.xml" to """
                <?xml version="1.0"?>
                <container version="1.0"
                    xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles>
                    <rootfile full-path="OEBPS/content.opf"
                        media-type="application/oebps-package+xml"/>
                  </rootfiles>
                </container>
            """.trimIndent(),
            "OEBPS/content.opf" to """
                <?xml version="1.0"?>
                <package version="3.0" xmlns="http://www.idpf.org/2007/opf">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                    <dc:title>Fixture</dc:title>
                    <dc:language>en</dc:language>
                  </metadata>
                  <manifest>$manifest</manifest>
                  <spine>$spineRefs</spine>
                </package>
            """.trimIndent(),
        )
        documents.forEach { (name, html) -> entries["OEBPS/$name"] = html }

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

    private fun page(body: String) = """
        <?xml version="1.0" encoding="utf-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml"
            xmlns:epub="http://www.idpf.org/2007/ops"><body>$body</body></html>
    """.trimIndent()

    // endregion
}
