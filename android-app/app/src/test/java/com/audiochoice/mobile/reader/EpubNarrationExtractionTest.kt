package com.audiochoice.mobile.reader

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
 * Extraction is where every offset in the feature originates, so these tests are
 * about two things: that the offsets index Book_Text exactly, and that Book_Text
 * is stable for a given archive. Everything downstream -- chapter boundaries,
 * filter ranges, reader highlighting, timeline round-trips -- is meaningless if
 * either fails.
 */
class EpubNarrationExtractionTest {

    // region encryption classification

    /**
     * An encrypted spine document means the text cannot be read, so the book is
     * declined. Extraction must stop before converting anything, because the
     * validator has to be able to report the decline without having extracted any
     * of the book's text.
     */
    @Test
    fun `adept encrypted spine document is reported as store drm with no text extracted`() {
        val document = read(
            epub(
                spine = listOf("chapter1.xhtml" to chapterHtml("Chapter One", "Encrypted prose.")),
                encryption = encryptionXml(
                    listOf("OEBPS/chapter1.xhtml"),
                    algorithm = "http://www.w3.org/2001/04/xmlenc#aes256-cbc",
                ),
            ),
        )

        assertTrue(document.carriesStoreDrm)
        assertEquals("", document.text)
        assertTrue(document.resources.isEmpty())
        assertEquals(
            listOf(TextResourceRole.SPINE_DOCUMENT),
            document.storeDrmResources.map { it.role },
        )
    }

    /** An encrypted package document is equally fatal and reported as its own role. */
    @Test
    fun `encrypted package document is reported as store drm`() {
        val document = read(
            epub(
                spine = listOf("chapter1.xhtml" to chapterHtml("One", "Prose.")),
                encryption = encryptionXml(listOf("OEBPS/content.opf")),
            ),
        )

        assertTrue(document.carriesStoreDrm)
        assertEquals(
            listOf(TextResourceRole.PACKAGE_DOCUMENT),
            document.storeDrmResources.map { it.role },
        )
    }

    /**
     * Font obfuscation is the common case and it says nothing about the text. A
     * book that obfuscates only its fonts reads perfectly, so declining it would
     * turn away a large share of legitimately purchased files.
     */
    @Test
    fun `font obfuscated archive is accepted and its text extracted`() {
        val document = read(
            epub(
                spine = listOf("chapter1.xhtml" to chapterHtml("One", "The fox jumped.")),
                extraEntries = mapOf("OEBPS/fonts/body.otf" to "not really a font"),
                encryption = encryptionXml(
                    listOf("OEBPS/fonts/body.otf"),
                    algorithm = "http://www.idpf.org/2008/embedding",
                ),
            ),
        )

        assertFalse(document.carriesStoreDrm)
        assertTrue(document.text.contains("The fox jumped."))
        assertTrue(document.encryptedEntries.any { it.endsWith("body.otf") })
    }

    /** Same reasoning for encrypted images: they contribute nothing to Book_Text. */
    @Test
    fun `image only encryption is accepted and the image excluded`() {
        val document = read(
            epub(
                spine = listOf("chapter1.xhtml" to chapterHtml("One", "Readable prose here.")),
                extraEntries = mapOf("OEBPS/images/plate.jpg" to "binary-ish"),
                encryption = encryptionXml(listOf("OEBPS/images/plate.jpg")),
            ),
        )

        assertFalse(document.carriesStoreDrm)
        assertTrue(document.text.contains("Readable prose here."))
    }

    /** No encryption declaration at all is the ordinary case. */
    @Test
    fun `archive without an encryption declaration carries no encrypted entries`() {
        val document = read(epub(spine = listOf("chapter1.xhtml" to chapterHtml("One", "Plain."))))

        assertTrue(document.encryptedEntries.isEmpty())
        assertFalse(document.carriesStoreDrm)
    }

    // endregion

    // region offsets index Book_Text exactly

    /**
     * The property everything else rests on: a recorded range, sliced out of
     * Book_Text, is the text that range was recorded for. If this drifts, a filter
     * removes the wrong passage and a highlight lands on the wrong sentence.
     */
    @Test
    fun `resource spans slice out the text of their own document`() {
        val document = read(
            epub(
                spine = listOf(
                    "chapter1.xhtml" to chapterHtml("Chapter One", "The first chapter body."),
                    "chapter2.xhtml" to chapterHtml("Chapter Two", "The second chapter body."),
                ),
            ),
        )

        assertEquals(2, document.resources.size)
        val first = document.resources[0]
        val second = document.resources[1]

        val firstText = document.text.substring(first.range.start, first.range.end)
        val secondText = document.text.substring(second.range.start, second.range.end)

        assertTrue(firstText.contains("The first chapter body."))
        assertFalse(firstText.contains("second chapter body"))
        assertTrue(secondText.contains("The second chapter body."))
        assertTrue(first.range.end <= second.range.start)
    }

    /** Ranges are ordered, non-overlapping and within Book_Text. */
    @Test
    fun `resource spans are ordered and bounded by the text length`() {
        val document = read(
            epub(
                spine = (1..5).map { index ->
                    "chapter$index.xhtml" to chapterHtml("Chapter $index", "Body number $index.")
                },
            ),
        )

        var previousEnd = 0
        document.resources.forEach { resource ->
            assertTrue(resource.range.start >= previousEnd)
            assertTrue(resource.range.end <= document.text.length)
            assertTrue(resource.range.start < resource.range.end)
            previousEnd = resource.range.end
        }
    }

    /**
     * A chapter can begin partway through a spine document, which is common in
     * single-file EPUBs. Without an anchor offset those chapters collapse into one.
     */
    @Test
    fun `anchor offsets point at the start of their own element`() {
        val document = read(
            epub(
                spine = listOf(
                    "all.xhtml" to """
                        <html><body>
                        <section id="one"><h1>One</h1><p>First part text.</p></section>
                        <section id="two"><h1>Two</h1><p>Second part text.</p></section>
                        </body></html>
                    """.trimIndent(),
                ),
            ),
        )

        val secondOffset = document.anchorOffsets["oebps/all.xhtml#two"]
        assertNotNull(secondOffset)
        val fromAnchor = document.text.substring(secondOffset!!)
        assertTrue(fromAnchor.startsWith("Two"))
        assertTrue(fromAnchor.contains("Second part text."))
        assertFalse(fromAnchor.contains("First part text."))
    }

    // endregion

    // region non-prose classification

    /** Tables, code and captions are structure, not narration. */
    @Test
    fun `structural elements are recorded as non prose`() {
        val document = read(
            epub(
                spine = listOf(
                    "chapter1.xhtml" to """
                        <html><body>
                        <p>Real prose before.</p>
                        <table><tr><td>Cell one</td><td>Cell two</td></tr></table>
                        <p>Real prose after.</p>
                        </body></html>
                    """.trimIndent(),
                ),
            ),
        )

        val nonProseText = document.nonProseRanges
            .joinToString(" ") { document.text.substring(it.start, it.end) }

        assertTrue(nonProseText.contains("Cell one"))
        assertFalse(nonProseText.contains("Real prose before."))
        assertFalse(nonProseText.contains("Real prose after."))
    }

    /**
     * Front matter is classified from the EPUB's own declared semantics rather
     * than guessed from a keyword, which is the whole reason narration uses a
     * separate extraction profile from `read()`.
     */
    @Test
    fun `declared front matter is classified as non prose without being dropped`() {
        val document = read(
            epub(
                spine = listOf(
                    "title.xhtml" to
                        """<html><body epub:type="titlepage"><p>A Novel By Someone</p></body></html>""",
                    "chapter1.xhtml" to chapterHtml("Chapter One", "The story starts here."),
                ),
            ),
        )

        // Retained in Book_Text, so the reader can still display it...
        assertTrue(document.text.contains("A Novel By Someone"))
        // ...but classified, so it is never narrated.
        val nonProseText = document.nonProseRanges
            .joinToString(" ") { document.text.substring(it.start, it.end) }
        assertTrue(nonProseText.contains("A Novel By Someone"))
        assertFalse(nonProseText.contains("The story starts here."))
    }

    /** Footnotes are marked by role as well as by EPUB semantics. */
    @Test
    fun `aria footnote role is classified as non prose`() {
        val document = read(
            epub(
                spine = listOf(
                    "chapter1.xhtml" to """
                        <html><body>
                        <p>Sentence in the story.</p>
                        <aside role="doc-footnote"><p>A footnote nobody reads aloud.</p></aside>
                        </body></html>
                    """.trimIndent(),
                ),
            ),
        )

        val nonProseText = document.nonProseRanges
            .joinToString(" ") { document.text.substring(it.start, it.end) }
        assertTrue(nonProseText.contains("A footnote nobody reads aloud."))
        assertFalse(nonProseText.contains("Sentence in the story."))
    }

    /** Ranges come back merged, so overlapping markup does not double-count. */
    @Test
    fun `non prose ranges are merged and ordered`() {
        val document = read(
            epub(
                spine = listOf(
                    "chapter1.xhtml" to """
                        <html><body>
                        <table><tr><td><code>nested</code></td></tr></table>
                        <p>Prose.</p>
                        <pre>preformatted</pre>
                        </body></html>
                    """.trimIndent(),
                ),
            ),
        )

        var previousEnd = -1
        document.nonProseRanges.forEach { range ->
            assertTrue(range.start > previousEnd)
            assertTrue(range.start < range.end)
            previousEnd = range.end
        }
    }

    // endregion

    // region navigation

    @Test
    fun `epub 3 navigation supplies top level entries only`() {
        val document = read(
            epub(
                spine = listOf(
                    "chapter1.xhtml" to chapterHtml("One", "Body one."),
                    "chapter2.xhtml" to chapterHtml("Two", "Body two."),
                ),
                navigation = """
                    <html><body>
                    <nav epub:type="toc">
                      <ol>
                        <li><a href="chapter1.xhtml">Chapter One</a>
                          <ol><li><a href="chapter1.xhtml#scene2">Scene Two</a></li></ol>
                        </li>
                        <li><a href="chapter2.xhtml">Chapter Two</a></li>
                      </ol>
                    </nav>
                    </body></html>
                """.trimIndent(),
            ),
        )

        val navigation = document.navigation
        assertNotNull(navigation)
        assertEquals(NavigationSource.EPUB3_NAV, navigation!!.source)
        assertEquals(listOf("Chapter One", "Chapter Two"), navigation.entries.map { it.title })
        assertEquals("oebps/chapter1.xhtml", navigation.entries[0].targetEntry)
    }

    /** A fragment target is preserved, since it is what splits one file in two. */
    @Test
    fun `navigation preserves fragment targets`() {
        val document = read(
            epub(
                spine = listOf("all.xhtml" to """<html><body><p id="two">Part two.</p></body></html>"""),
                navigation = """
                    <html><body><nav epub:type="toc"><ol>
                      <li><a href="all.xhtml#two">Part Two</a></li>
                    </ol></nav></body></html>
                """.trimIndent(),
            ),
        )

        assertEquals("two", document.navigation?.entries?.single()?.targetFragment)
    }

    /** With no EPUB 3 nav, the NCX is the fallback before the spine. */
    @Test
    fun `ncx supplies entries when there is no navigation document`() {
        val document = read(
            epub(
                spine = listOf(
                    "chapter1.xhtml" to chapterHtml("One", "Body one."),
                    "chapter2.xhtml" to chapterHtml("Two", "Body two."),
                ),
                ncx = """
                    <ncx><navMap>
                      <navPoint><navLabel><text>First</text></navLabel>
                        <content src="chapter1.xhtml"/>
                        <navPoint><navLabel><text>Nested</text></navLabel>
                          <content src="chapter1.xhtml#x"/></navPoint>
                      </navPoint>
                      <navPoint><navLabel><text>Second</text></navLabel>
                        <content src="chapter2.xhtml"/></navPoint>
                    </navMap></ncx>
                """.trimIndent(),
            ),
        )

        val navigation = document.navigation
        assertNotNull(navigation)
        assertEquals(NavigationSource.NCX, navigation!!.source)
        assertEquals(listOf("First", "Second"), navigation.entries.map { it.title })
    }

    /** No navigation of either kind is legitimate; the caller falls back to the spine. */
    @Test
    fun `archive without navigation reports none`() {
        val document = read(epub(spine = listOf("chapter1.xhtml" to chapterHtml("One", "Body."))))

        assertNull(document.navigation)
        assertEquals(1, document.declaredSpineEntries.size)
    }

    /** A navigation document inside the spine is a contents list, not narration. */
    @Test
    fun `navigation document in the spine is classified as non prose`() {
        val document = read(
            epub(
                spine = listOf(
                    "nav.xhtml" to """
                        <html><body><nav epub:type="toc"><ol>
                          <li><a href="chapter1.xhtml">Chapter One</a></li>
                        </ol></nav></body></html>
                    """.trimIndent(),
                    "chapter1.xhtml" to chapterHtml("Chapter One", "Story body."),
                ),
                navigationEntryName = "nav.xhtml",
            ),
        )

        val nonProseText = document.nonProseRanges
            .joinToString(" ") { document.text.substring(it.start, it.end) }
        assertTrue(nonProseText.contains("Chapter One"))
        assertFalse(nonProseText.contains("Story body."))
    }

    // endregion

    // region stability and metadata

    /**
     * A plan records the hash of the text it was built against. If extraction were
     * not deterministic, every reopen would look like the book had changed and
     * would discard rendered audio.
     */
    @Test
    fun `book text is byte identical across repeated reads`() {
        val archive = epub(
            spine = listOf(
                "chapter1.xhtml" to chapterHtml("One", "Stable text with &amp; entities and  spacing."),
                "chapter2.xhtml" to chapterHtml("Two", "More stable text."),
            ),
        )

        val first = read(archive)
        val second = read(archive)
        val third = read(archive)

        assertEquals(first.text, second.text)
        assertEquals(second.text, third.text)
        assertEquals(first.nonProseRanges, third.nonProseRanges)
        assertEquals(first.anchorOffsets, third.anchorOffsets)
    }

    @Test
    fun `package metadata is read from the first title and creator in document order`() {
        val document = read(
            epub(
                spine = listOf("chapter1.xhtml" to chapterHtml("One", "Body.")),
                title = "The Real Title",
                author = "First Author",
                extraMetadata = "<dc:title>An Alternate Title</dc:title>" +
                    "<dc:creator>Second Author</dc:creator>",
            ),
        )

        assertEquals("The Real Title", document.title)
        assertEquals("First Author", document.author)
        assertEquals("en", document.language)
    }

    @Test
    fun `entities and whitespace are normalised during extraction`() {
        val document = read(
            epub(
                spine = listOf(
                    "chapter1.xhtml" to
                        "<html><body><p>Tom&apos;s   caf&eacute;&mdash;open&#33;</p></body></html>",
                ),
            ),
        )

        assertTrue(document.text.contains("Tom's café—open!"))
        assertFalse(document.text.contains("  "))
    }

    /** Script and style content is markup, never prose. */
    @Test
    fun `script and style content is excluded from book text`() {
        val document = read(
            epub(
                spine = listOf(
                    "chapter1.xhtml" to """
                        <html><head><title>Not prose</title><style>p { color: red }</style></head>
                        <body><script>var secret = 1;</script><p>Only this is prose.</p></body></html>
                    """.trimIndent(),
                ),
            ),
        )

        assertEquals("Only this is prose.", document.text)
    }

    /** A missing spine document is recorded rather than silently skipped. */
    @Test
    fun `absent spine document is reported as unreadable`() {
        val document = read(
            epub(
                spine = listOf("chapter1.xhtml" to chapterHtml("One", "Body.")),
                declareExtraSpineEntry = "missing.xhtml",
            ),
        )

        assertEquals(listOf("oebps/missing.xhtml"), document.unreadableSpineEntries)
        assertEquals(1, document.resources.size)
    }

    @Test
    fun `archive without a container is unreadable`() {
        val bytes = zip(mapOf("OEBPS/chapter1.xhtml" to chapterHtml("One", "Body.")))

        val document = read(bytes)

        assertEquals("", document.text)
        assertTrue(document.declaredSpineEntries.isEmpty())
    }

    @Test
    fun `letter or digit count ignores punctuation and whitespace`() {
        val document = read(
            epub(spine = listOf("chapter1.xhtml" to "<html><body><p>ab, cd. 12!</p></body></html>")),
        )

        assertEquals(6, document.letterOrDigitCount)
    }

    /**
     * The two extraction profiles differ, deliberately, and this is the difference.
     *
     * `read()` drops leading pages until one matches a story-start keyword, which
     * is what a read-along wants. Narration keeps every page so the reader can
     * still display front matter, and relies on declared semantics to decide what
     * is narrated. `read()` is left untouched because imported-audiobook reader
     * alignments are cached against a version constant, and changing the text they
     * were computed from would invalidate every cached alignment already on a
     * device.
     */
    @Test
    fun `narration extraction keeps front matter that the read path would drop`() {
        val archive = epub(
            spine = listOf(
                "front.xhtml" to "<html><body><p>Copyright notice page.</p></body></html>",
                "chapter1.xhtml" to "<html><body><p>Chapter One</p><p>Story begins.</p></body></html>",
            ),
        )

        val document = read(archive)

        assertTrue(document.text.contains("Copyright notice page."))
        assertTrue(document.text.contains("Story begins."))
        assertEquals(2, document.resources.size)
    }

    // endregion

    // region fixtures

    private fun read(archive: ByteArray): EpubDocument =
        EpubTextReader.readNarrationDocument(ByteArrayInputStream(archive))

    private fun chapterHtml(heading: String, body: String): String =
        "<html><body><h1>$heading</h1><p>$body</p></body></html>"

    /**
     * Builds a minimal but structurally real EPUB. Real archives are what these
     * tests are about: a hand-made map of strings would not exercise the container,
     * manifest, spine and path resolution that extraction actually depends on.
     */
    private fun epub(
        spine: List<Pair<String, String>>,
        navigation: String? = null,
        navigationEntryName: String = "nav.xhtml",
        ncx: String? = null,
        title: String = "Test Book",
        author: String = "Test Author",
        extraMetadata: String = "",
        extraEntries: Map<String, String> = emptyMap(),
        encryption: String? = null,
        declareExtraSpineEntry: String? = null,
    ): ByteArray {
        val manifestItems = StringBuilder()
        val spineItems = StringBuilder()

        spine.forEachIndexed { index, (name, _) ->
            manifestItems.append(
                """<item id="s$index" href="$name" media-type="application/xhtml+xml"/>""",
            )
            spineItems.append("""<itemref idref="s$index"/>""")
        }
        declareExtraSpineEntry?.let {
            manifestItems.append("""<item id="missing" href="$it" media-type="application/xhtml+xml"/>""")
            spineItems.append("""<itemref idref="missing"/>""")
        }
        if (navigation != null && spine.none { it.first == navigationEntryName }) {
            manifestItems.append(
                """<item id="nav" href="$navigationEntryName" """ +
                    """media-type="application/xhtml+xml" properties="nav"/>""",
            )
        } else if (navigation != null) {
            manifestItems.append(
                """<item id="nav" href="$navigationEntryName" """ +
                    """media-type="application/xhtml+xml" properties="nav"/>""",
            )
        }
        if (spine.any { it.first == navigationEntryName } && navigation == null) {
            manifestItems.append(
                """<item id="nav" href="$navigationEntryName" """ +
                    """media-type="application/xhtml+xml" properties="nav"/>""",
            )
        }
        val ncxAttribute = if (ncx != null) """ toc="ncx"""" else ""
        if (ncx != null) {
            manifestItems.append("""<item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>""")
        }

        val opf = """
            <?xml version="1.0"?>
            <package version="3.0" xmlns="http://www.idpf.org/2007/opf">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>$title</dc:title>
                <dc:creator>$author</dc:creator>
                <dc:language>en</dc:language>
                $extraMetadata
              </metadata>
              <manifest>$manifestItems</manifest>
              <spine$ncxAttribute>$spineItems</spine>
            </package>
        """.trimIndent()

        val entries = linkedMapOf(
            "META-INF/container.xml" to """
                <?xml version="1.0"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles>
                    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
                  </rootfiles>
                </container>
            """.trimIndent(),
            "OEBPS/content.opf" to opf,
        )
        spine.forEach { (name, html) -> entries["OEBPS/$name"] = html }
        navigation?.let { entries["OEBPS/$navigationEntryName"] = it }
        ncx?.let { entries["OEBPS/toc.ncx"] = it }
        encryption?.let { entries["META-INF/encryption.xml"] = it }
        entries.putAll(extraEntries)

        return zip(entries)
    }

    private fun encryptionXml(
        uris: List<String>,
        algorithm: String = "http://www.w3.org/2001/04/xmlenc#aes256-cbc",
    ): String {
        val data = uris.joinToString("") { uri ->
            """
            <EncryptedData xmlns="http://www.w3.org/2001/04/xmlenc#">
              <EncryptionMethod Algorithm="$algorithm"/>
              <CipherData><CipherReference URI="$uri"/></CipherData>
            </EncryptedData>
            """.trimIndent()
        }
        return """<?xml version="1.0"?><encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container">$data</encryption>"""
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
