package com.audiochoice.mobile.narration

import com.audiochoice.mobile.reader.EpubTextReader
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayOutputStream
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

/**
 * A short line repeating across most of a book's documents is furniture, not prose.
 *
 * Publishers, conversion tools and distribution sites all append the same line to every file they
 * touch. It is text, so extraction keeps it, and it is not prose, so a voice reads it out at the end
 * of every chapter. One real book carried such a line in all 186 of its documents, so the narrator
 * said the same six syllables 173 times.
 *
 * Identified by how a line behaves, never by what it says. A list of known strings would need
 * extending for every new source and would explain nothing about why the line is not prose.
 */
class RepeatedBoilerplateTest {

    @Test
    fun `a short line repeated across most documents is not narrated`() {
        val plan = planFor(
            List(6) { index -> "<p>Chapter ${index + 1} begins here and continues.</p><p>SomeSite.com</p>" },
        )
        val spoken = spokenText(plan)
        assertFalse(
            "the repeated line is still narrated, so the voice says it after every chapter",
            spoken.contains("SomeSite.com"),
        )
        // The book itself must survive.
        assertTrue("the prose was silenced too", spoken.contains("Chapter 1 begins here"))
        assertTrue("the prose was silenced too", spoken.contains("Chapter 6 begins here"))
    }

    /**
     * A long repeated passage is left alone.
     *
     * A refrain, a recurring epigraph or a chapter-opening quotation is something the author wrote,
     * and length is what separates those from furniture.
     */
    @Test
    fun `a long repeated passage is still narrated`() {
        val refrain = "The lake remembers every name it has ever swallowed and it will " +
            "remember yours as well before the summer turns, or so the old men at the " +
            "landing liked to say whenever a stranger asked about the water."
        val plan = planFor(List(6) { index -> "<p>Chapter ${index + 1}.</p><p>$refrain</p>" })
        assertTrue(
            "a long repeated passage was silenced, which removes text the author wrote",
            spokenText(plan).contains("The lake remembers every name"),
        )
    }

    /**
     * A line in only a couple of chapters is prose.
     *
     * Two chapters ending the same way is a thing authors do; nearly all of them ending the same way
     * is not.
     */
    @Test
    fun `a line appearing in a minority of documents is still narrated`() {
        val documents = List(6) { index ->
            val tail = if (index < 2) "<p>And so it went.</p>" else ""
            "<p>Chapter ${index + 1} begins here and continues.</p>$tail"
        }
        assertTrue(
            "a line in two of six chapters was silenced on far too little evidence",
            spokenText(planFor(documents)).contains("And so it went"),
        )
    }

    /**
     * A very short book silences nothing.
     *
     * With two documents, "most of them" is one, and a genuinely repeated line would be removed on
     * no evidence at all.
     */
    @Test
    fun `a book of two documents silences nothing`() {
        val plan = planFor(
            List(2) { index -> "<p>Chapter ${index + 1} begins here and continues.</p><p>SomeSite.com</p>" },
        )
        assertTrue(
            "a two-document book had a line silenced, where the sample cannot support it",
            spokenText(plan).contains("SomeSite.com"),
        )
    }

    /**
     * Every occurrence goes, not just the repeats.
     *
     * Silencing all but the first would leave the narrator saying it once, at the end of the first
     * chapter, which is the least explicable outcome available.
     */
    @Test
    fun `no occurrence of the repeated line survives anywhere`() {
        val plan = planFor(
            List(6) { index -> "<p>SomeSite.com</p><p>Chapter ${index + 1} begins.</p><p>SomeSite.com</p>" },
        )
        assertEquals(
            "an occurrence of the repeated line survived",
            0,
            Regex("SomeSite").findAll(spokenText(plan)).count(),
        )
    }

    // region fixtures

    private fun spokenText(plan: com.audiochoice.mobile.data.NarrationPlan) =
        plan.chapters.joinToString(" ") { chapter ->
            chapter.units.joinToString(" ") { it.sourceCharacters }
        }

    private fun planFor(bodies: List<String>): com.audiochoice.mobile.data.NarrationPlan {
        val document = epub(bodies).inputStream().use {
            EpubTextReader.readNarrationDocument(it)
        }
        return requireNotNull(
            StructureParser.buildPlan(
                document = document,
                sourceSha256 = "a".repeat(64),
                synthesisInputLimit = 3_000,
            ),
        ) { "the fixture produced no plan at all" }
    }

    private fun epub(bodies: List<String>): ByteArray {
        val manifest = StringBuilder()
        val spineRefs = StringBuilder()
        bodies.indices.forEach { index ->
            manifest.append(
                """<item id="s$index" href="chapter_$index.xhtml"
                    media-type="application/xhtml+xml"/>""",
            )
            spineRefs.append("""<itemref idref="s$index"/>""")
        }
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
        bodies.forEachIndexed { index, body ->
            entries["OEBPS/chapter_$index.xhtml"] = """
                <?xml version="1.0" encoding="utf-8"?>
                <html xmlns="http://www.w3.org/1999/xhtml"><body>$body</body></html>
            """.trimIndent()
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
