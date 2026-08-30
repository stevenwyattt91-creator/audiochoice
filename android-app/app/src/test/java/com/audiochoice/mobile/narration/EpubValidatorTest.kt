package com.audiochoice.mobile.narration

import com.audiochoice.mobile.reader.EpubTextReader
import com.audiochoice.mobile.reader.TextResourceRole
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

/**
 * The validator's job is to report one reason, and the right one. These tests
 * cover each branch and, more importantly, the cases where two conditions hold at
 * once: that is where an ordering change would quietly start telling a listener
 * with a DRM-protected file that their book is too short.
 */
class EpubValidatorTest {

    // region one reason per branch

    @Test
    fun `unreadable archive is declined as could not open`() {
        val declined = validate(byteArrayOf(1, 2, 3, 4, 5))

        assertEquals(DeclineReason.CouldNotOpen, declined)
    }

    @Test
    fun `archive without a container is declined as not an epub`() {
        val declined = validate(zip(mapOf("OEBPS/chapter1.xhtml" to page("Body."))))

        assertEquals(DeclineReason.NotAnEpub, declined)
    }

    @Test
    fun `container naming an absent package document is declined as not an epub`() {
        val declined = validate(
            zip(
                mapOf(
                    "META-INF/container.xml" to container("OEBPS/missing.opf"),
                    "OEBPS/chapter1.xhtml" to page("Body."),
                ),
            ),
        )

        assertEquals(DeclineReason.NotAnEpub, declined)
    }

    @Test
    fun `encrypted spine document is declined as store drm naming the chapter pages`() {
        val declined = validate(
            epub(
                spine = listOf("chapter1.xhtml" to page(longProse())),
                encryptedEntries = listOf("OEBPS/chapter1.xhtml"),
            ),
        )

        assertTrue(declined is DeclineReason.StoreDrm)
        assertEquals(
            listOf(TextResourceRole.SPINE_DOCUMENT),
            (declined as DeclineReason.StoreDrm).encryptedRoles,
        )
    }

    @Test
    fun `spine document absent from the archive is declined as text unreadable`() {
        val declined = validate(epub(spine = emptyList(), declaredButAbsent = listOf("gone.xhtml")))

        assertEquals(DeclineReason.TextUnreadable, declined)
    }

    @Test
    fun `short book is declined as too little text with the minimum stated`() {
        val declined = validate(epub(spine = listOf("chapter1.xhtml" to page("Only a few words."))))

        assertTrue(declined is DeclineReason.TooLittleText)
        val reason = declined as DeclineReason.TooLittleText
        assertEquals(EpubValidator.MINIMUM_LETTERS_OR_DIGITS, reason.minimum)
        assertTrue(reason.letterOrDigitCount < reason.minimum)
    }

    @Test
    fun `a readable book of sufficient length is accepted`() {
        val validation = EpubValidator.classify(
            EpubTextReader.readNarrationDocument(
                ByteArrayInputStream(epub(spine = listOf("chapter1.xhtml" to page(longProse())))),
            ),
        )

        assertTrue(validation is EpubValidation.Accepted)
        assertTrue((validation as EpubValidation.Accepted).document.letterOrDigitCount >= 500)
    }

    // endregion

    // region ordering when several conditions hold

    /**
     * The case that matters most. A DRM-protected file also has no readable text
     * and no length, so all three later checks would fire. Reporting the length
     * would send someone looking for a longer copy of a book they already own.
     */
    @Test
    fun `store drm outranks unreadable text and length`() {
        val declined = validate(
            epub(
                spine = listOf("chapter1.xhtml" to page("Tiny.")),
                encryptedEntries = listOf("OEBPS/chapter1.xhtml"),
            ),
        )

        assertTrue(declined is DeclineReason.StoreDrm)
    }

    /** A missing package document outranks store DRM: nothing could be classified yet. */
    @Test
    fun `missing package document outranks store drm`() {
        val declined = validate(
            zip(
                mapOf(
                    "META-INF/container.xml" to container("OEBPS/missing.opf"),
                    "META-INF/encryption.xml" to encryption(listOf("OEBPS/chapter1.xhtml")),
                    "OEBPS/chapter1.xhtml" to page(longProse()),
                ),
            ),
        )

        assertEquals(DeclineReason.NotAnEpub, declined)
    }

    /** An unopenable archive outranks everything, because nothing was read at all. */
    @Test
    fun `could not open outranks every other reason`() {
        val declined = validate("this is plain text, not a zip".toByteArray())

        assertEquals(DeclineReason.CouldNotOpen, declined)
    }

    /** Unreadable text outranks length: there is no text to measure. */
    @Test
    fun `unreadable text outranks too little text`() {
        val declined = validate(epub(spine = emptyList(), declaredButAbsent = listOf("gone.xhtml")))

        assertEquals(DeclineReason.TextUnreadable, declined)
    }

    // endregion

    // region nothing retained on a decline

    /**
     * The purge rule is enforced by the type rather than by remembering to clear
     * something: a declined result has no document field, so there is no path by
     * which a caller could persist extracted text for a rejected file.
     */
    @Test
    fun `declined results expose no extracted document`() {
        val validation = EpubValidator.classify(
            EpubTextReader.readNarrationDocument(
                ByteArrayInputStream(
                    epub(
                        spine = listOf("chapter1.xhtml" to page(longProse())),
                        encryptedEntries = listOf("OEBPS/chapter1.xhtml"),
                    ),
                ),
            ),
        )

        assertTrue(validation is EpubValidation.Declined)
        val fields = EpubValidation.Declined::class.java.declaredFields.map { it.name }
        assertFalse("A declined result must not carry the document", fields.contains("document"))
    }

    /**
     * Store-DRM detection runs before any chapter is converted, so a declined
     * protected file never produced text in the first place. That is what makes
     * "retain nothing" true rather than aspirational.
     */
    @Test
    fun `store drm decline extracted no text at all`() {
        val document = EpubTextReader.readNarrationDocument(
            ByteArrayInputStream(
                epub(
                    spine = listOf("chapter1.xhtml" to page(longProse())),
                    encryptedEntries = listOf("OEBPS/chapter1.xhtml"),
                ),
            ),
        )

        assertTrue(document.carriesStoreDrm)
        assertEquals("", document.text)
        assertTrue(document.resources.isEmpty())
        assertTrue(document.anchorOffsets.isEmpty())
        assertTrue(document.nonProseRanges.isEmpty())
    }

    // endregion

    // region performance

    /**
     * The bound in the requirement is five seconds for a file up to 100 MB. A
     * 100 MB fixture would need several hundred megabytes of heap once decoded to
     * UTF-16, which is not something to impose on every test run, so this uses a
     * few megabytes of prose across many chapters and leaves the full-size bound to
     * a device measurement. It still catches the failure that matters here:
     * accidental quadratic behaviour in the extraction pass.
     */
    @Test
    fun `validation of a multi megabyte book completes well inside the bound`() {
        val paragraph = longProse()
        val archive = epub(
            spine = (1..300).map { index -> "chapter$index.xhtml" to page(paragraph.repeat(6)) },
        )

        val start = System.nanoTime()
        val validation = EpubValidator.classify(
            EpubTextReader.readNarrationDocument(ByteArrayInputStream(archive)),
        )
        val elapsedMs = (System.nanoTime() - start) / 1_000_000

        assertTrue(validation is EpubValidation.Accepted)
        assertTrue("extraction and validation took ${elapsedMs}ms", elapsedMs < 5_000)
    }

    // endregion

    // region decline copy

    /**
     * The Kindle guidance is the point of this surface. Someone whose library is on
     * Kindle needs to learn which of their books can work rather than concluding
     * the app is broken, so the copy has to distinguish a purchase from a Kindle
     * Unlimited borrow and has to say whose choice the protection was.
     */
    @Test
    fun `store drm copy names sources, the amazon route, and kindle unlimited`() {
        val message = DeclineMessages.forReason(
            DeclineReason.StoreDrm(
                encryptedRoles = listOf(TextResourceRole.SPINE_DOCUMENT),
                encryptedEntries = listOf("oebps/chapter1.xhtml"),
            ),
        )

        assertTrue(message.drmFreeSources.size >= 3)
        assertTrue(message.details.single().contains("chapter pages"))
        assertTrue(message.kindleGuidance.any { it.contains("Manage Your Content and Devices") })
        assertTrue(message.kindleGuidance.any { it.contains("Kindle Unlimited") })
        assertTrue(message.kindleGuidance.any { it.contains("borrowed rather than bought") })
        assertTrue(message.explanation.contains("publisher's or the author's choice"))
        assertEquals(
            DeclineMessages.AMAZON_CONTENT_AND_DEVICES_URL,
            message.actions.first { it.url != null }.url,
        )
    }

    /** Several encrypted roles read as a sentence rather than a list of enum names. */
    @Test
    fun `encrypted document names are joined readably`() {
        val message = DeclineMessages.forReason(
            DeclineReason.StoreDrm(
                encryptedRoles = listOf(
                    TextResourceRole.PACKAGE_DOCUMENT,
                    TextResourceRole.SPINE_DOCUMENT,
                ),
                encryptedEntries = emptyList(),
            ),
        )

        assertEquals(
            "Encrypted in this file: the package document and the chapter pages.",
            message.details.single(),
        )
    }

    /**
     * Only the store-DRM decline carries Kindle guidance. Telling someone whose
     * download was truncated about Kindle Unlimited would be noise.
     */
    @Test
    fun `only the store drm decline carries kindle guidance`() {
        val reasons = listOf(
            DeclineReason.CouldNotOpen,
            DeclineReason.NotAnEpub,
            DeclineReason.TextUnreadable,
            DeclineReason.TooLittleText(12, 500),
        )

        reasons.forEach { reason ->
            assertTrue(
                reason.toString(),
                DeclineMessages.forReason(reason).kindleGuidance.isEmpty(),
            )
        }
    }

    /** Every reason says something, and offers a way forward. */
    @Test
    fun `every decline reason has a headline, an explanation and an action`() {
        val reasons = listOf(
            DeclineReason.CouldNotOpen,
            DeclineReason.NotAnEpub,
            DeclineReason.TextUnreadable,
            DeclineReason.TooLittleText(12, 500),
            DeclineReason.StoreDrm(listOf(TextResourceRole.SPINE_DOCUMENT), emptyList()),
        )

        reasons.forEach { reason ->
            val message = DeclineMessages.forReason(reason)
            assertTrue(reason.toString(), message.headline.isNotBlank())
            assertTrue(reason.toString(), message.explanation.isNotBlank())
            assertTrue(reason.toString(), message.actions.isNotEmpty())
        }
    }

    /** The stated count has to be the real count, or the message misleads. */
    @Test
    fun `too little text copy states the actual and required counts`() {
        val message = DeclineMessages.forReason(DeclineReason.TooLittleText(137, 500))

        assertTrue(message.explanation.contains("137"))
        assertTrue(message.explanation.contains("500"))
    }

    // endregion

    // region fixtures

    private fun validate(archive: ByteArray): DeclineReason {
        val validation = EpubValidator.classify(
            EpubTextReader.readNarrationDocument(ByteArrayInputStream(archive)),
        )
        assertTrue("expected a decline but got $validation", validation is EpubValidation.Declined)
        return (validation as EpubValidation.Declined).reason
    }

    private fun page(body: String) = "<html><body><p>$body</p></body></html>"

    /** Comfortably past the 500 letter-or-digit floor, so length is never the reason. */
    private fun longProse(): String =
        ("The lantern swung against the rigging and the deck went silver, then dark, " +
            "then silver again as the swell lifted and dropped beneath them all night. ")
            .repeat(5)

    private fun container(opfPath: String) = """
        <?xml version="1.0"?>
        <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
          <rootfiles>
            <rootfile full-path="$opfPath" media-type="application/oebps-package+xml"/>
          </rootfiles>
        </container>
    """.trimIndent()

    private fun encryption(uris: List<String>) = """
        <?xml version="1.0"?>
        <encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
        ${
        uris.joinToString("") { uri ->
            """
            <EncryptedData xmlns="http://www.w3.org/2001/04/xmlenc#">
              <EncryptionMethod Algorithm="http://www.w3.org/2001/04/xmlenc#aes256-cbc"/>
              <CipherData><CipherReference URI="$uri"/></CipherData>
            </EncryptedData>
            """.trimIndent()
        }
    }
        </encryption>
    """.trimIndent()

    private fun epub(
        spine: List<Pair<String, String>>,
        declaredButAbsent: List<String> = emptyList(),
        encryptedEntries: List<String> = emptyList(),
    ): ByteArray {
        val manifest = StringBuilder()
        val spineRefs = StringBuilder()
        spine.forEachIndexed { index, (name, _) ->
            manifest.append("""<item id="s$index" href="$name" media-type="application/xhtml+xml"/>""")
            spineRefs.append("""<itemref idref="s$index"/>""")
        }
        declaredButAbsent.forEachIndexed { index, name ->
            manifest.append("""<item id="m$index" href="$name" media-type="application/xhtml+xml"/>""")
            spineRefs.append("""<itemref idref="m$index"/>""")
        }

        val entries = linkedMapOf(
            "META-INF/container.xml" to container("OEBPS/content.opf"),
            "OEBPS/content.opf" to """
                <?xml version="1.0"?>
                <package version="3.0" xmlns="http://www.idpf.org/2007/opf">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                    <dc:title>Fixture</dc:title>
                    <dc:creator>Fixture Author</dc:creator>
                    <dc:language>en</dc:language>
                  </metadata>
                  <manifest>$manifest</manifest>
                  <spine>$spineRefs</spine>
                </package>
            """.trimIndent(),
        )
        spine.forEach { (name, html) -> entries["OEBPS/$name"] = html }
        if (encryptedEntries.isNotEmpty()) {
            entries["META-INF/encryption.xml"] = encryption(encryptedEntries)
        }
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
