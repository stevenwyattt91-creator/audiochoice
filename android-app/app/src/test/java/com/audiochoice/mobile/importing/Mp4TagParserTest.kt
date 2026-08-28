package com.audiochoice.mobile.importing

import java.io.ByteArrayOutputStream
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Byte-level tests for the `ilst` metadata list. These build real atom layouts
 * because the point of the parser is to reach fields Android's own metadata
 * retriever cannot, so there is no higher-level API to test against.
 */
class Mp4TagParserTest {

    @Test
    fun `reads the standard text atoms`() {
        val tags = Mp4TagParser.parseIlst(
            ilst(
                textTag("\u00A9nam", "King Sorrow"),
                textTag("\u00A9ART", "Joe Hill"),
                textTag("\u00A9alb", "King Sorrow"),
                textTag("cprt", "(P) 2025 Blackstone"),
            ),
        )
        assertEquals("King Sorrow", tags.title)
        assertEquals("Joe Hill", tags.author)
        assertEquals("King Sorrow", tags.albumTitle)
        assertEquals("(P) 2025 Blackstone", tags.copyright)
    }

    /**
     * The whole reason this parser exists. ASIN lives in a freeform atom that
     * MediaMetadataRetriever has no way to expose, and it is the only definitive
     * edition key a file can carry.
     */
    @Test
    fun `reads a freeform ASIN and treats it as the product identifier`() {
        val tags = Mp4TagParser.parseIlst(ilst(freeformTag("ASIN", "B0CTJ1PDKM")))
        assertEquals("B0CTJ1PDKM", tags.asin)
        assertEquals("B0CTJ1PDKM", tags.productIdentifier)
    }

    @Test
    fun `normalizes identifiers so punctuation cannot break equality`() {
        val tags = Mp4TagParser.parseIlst(ilst(freeformTag("ISBN", "978-1-0987-6543-2")))
        assertEquals("9781098765432", tags.isbn)
        assertEquals("9781098765432", tags.productIdentifier)
    }

    @Test
    fun `prefers an ASIN over an ISBN as the identifier`() {
        val tags = Mp4TagParser.parseIlst(
            ilst(freeformTag("ISBN", "9781098765432"), freeformTag("ASIN", "B0CTJ1PDKM")),
        )
        assertEquals("B0CTJ1PDKM", tags.productIdentifier)
    }

    @Test
    fun `prefers a freeform narrator over the composer fallback`() {
        val tags = Mp4TagParser.parseIlst(
            ilst(textTag("\u00A9wrt", "Some Composer"), freeformTag("NARRATOR", "Zachary Quinto")),
        )
        assertEquals("Zachary Quinto", tags.narrator)
    }

    @Test
    fun `falls back to the composer atom when no narrator tag exists`() {
        val tags = Mp4TagParser.parseIlst(ilst(textTag("\u00A9wrt", "Zachary Quinto")))
        assertEquals("Zachary Quinto", tags.narrator)
    }

    @Test
    fun `takes the year out of a full timestamp`() {
        val tags = Mp4TagParser.parseIlst(ilst(textTag("\u00A9day", "2025-10-07T00:00:00Z")))
        assertEquals("2025", tags.year)
    }

    @Test
    fun `reads a series part written as prose`() {
        val tags = Mp4TagParser.parseIlst(
            ilst(freeformTag("SERIES", "Empyrean"), freeformTag("SERIES-PART", "Book 3")),
        )
        assertEquals("Empyrean", tags.seriesTitle)
        assertEquals(3, tags.seriesPart)
    }

    /** Integer-typed data atoms are as common as text ones for numeric tags. */
    @Test
    fun `reads an integer typed value`() {
        val tags = Mp4TagParser.parseIlst(
            ilst(freeformTag("SERIES-PART", intData(2))),
        )
        assertEquals(2, tags.seriesPart)
    }

    @Test
    fun `ignores binary payloads such as artwork`() {
        // Type 13 is JPEG. Decoding it as text would produce mojibake in the title.
        val artwork = atom("covr", atom("data", dataPayload(byteArrayOf(1, 2, 3, 4), wellKnownType = 13)))
        val tags = Mp4TagParser.parseIlst(ilst(artwork, textTag("\u00A9nam", "King Sorrow")))
        assertEquals("King Sorrow", tags.title)
    }

    @Test
    fun `decodes UTF-16 text`() {
        val payload = dataPayload("Königreich".toByteArray(Charsets.UTF_16BE), wellKnownType = 2)
        val tags = Mp4TagParser.parseIlst(ilst(atom("\u00A9nam", atom("data", payload))))
        assertEquals("Königreich", tags.title)
    }

    @Test
    fun `returns nothing for an empty list`() {
        assertTrue(Mp4TagParser.parseIlst(ByteArray(0)).isEmpty)
    }

    /**
     * Imported files are untrusted input. A truncated container must yield fewer
     * tags, never an exception.
     */
    @Test
    fun `survives a truncated atom`() {
        val complete = ilst(textTag("\u00A9nam", "King Sorrow"))
        val truncated = complete.copyOf(complete.size - 4)
        val tags = Mp4TagParser.parseIlst(truncated)
        assertNull(tags.title)
    }

    @Test
    fun `survives a size field that overruns the payload`() {
        val payload = ByteArrayOutputStream().apply {
            write(intBytes(9_999))
            write("\u00A9nam".toByteArray(Charsets.ISO_8859_1))
            write("King Sorrow".toByteArray(Charsets.UTF_8))
        }.toByteArray()
        assertTrue(Mp4TagParser.parseIlst(payload).isEmpty)
    }

    @Test
    fun `survives a zero size field without looping forever`() {
        val payload = ByteArrayOutputStream().apply {
            write(intBytes(0))
            write("\u00A9nam".toByteArray(Charsets.ISO_8859_1))
        }.toByteArray()
        // Size 0 means "to the end of the parent", so this yields no `data` child.
        assertNull(Mp4TagParser.parseIlst(payload).title)
    }

    // ---- atom builders ----

    /**
     * The synopsis is what the Explore screen shows under "About this audiobook", so a
     * wrong value here is prose a listener reads and believes.
     */
    @Test
    fun `reads the publisher synopsis from the long description atom`() {
        val tags = Mp4TagParser.parseIlst(ilst(textTag("ldes", LONG_SYNOPSIS)))
        assertEquals(LONG_SYNOPSIS, tags.synopsis)
    }

    @Test
    fun `prefers the long description over the short one`() {
        val tags = Mp4TagParser.parseIlst(
            ilst(
                textTag("\u00A9des", "A dragon rider goes to war. She may not survive it."),
                textTag("ldes", LONG_SYNOPSIS),
            ),
        )
        assertEquals(LONG_SYNOPSIS, tags.synopsis)
    }

    @Test
    fun `falls back to a freeform description atom`() {
        val tags = Mp4TagParser.parseIlst(ilst(freeformTag("description", LONG_SYNOPSIS)))
        assertEquals(LONG_SYNOPSIS, tags.synopsis)
    }

    /**
     * Converters write their own name into these atoms. Presenting that as the story is
     * worse than presenting nothing, because the screen says "About this audiobook".
     */
    @Test
    fun `rejects an encoder credit masquerading as a synopsis`() {
        val tags = Mp4TagParser.parseIlst(
            ilst(textTag("\u00A9cmt", "Created by Libation, the Audible library exporter tool")),
        )
        assertNull(tags.synopsis)
    }

    @Test
    fun `rejects a description too short to be a synopsis`() {
        assertNull(Mp4TagParser.parseIlst(ilst(textTag("ldes", "Fantasy"))).synopsis)
    }

    @Test
    fun `reports no synopsis when the file carries none`() {
        assertNull(Mp4TagParser.parseIlst(ilst(textTag("\u00A9nam", "King Sorrow"))).synopsis)
    }

    private fun ilst(vararg atoms: ByteArray): ByteArray =
        ByteArrayOutputStream().apply { atoms.forEach(::write) }.toByteArray()

    private fun atom(type: String, payload: ByteArray): ByteArray =
        ByteArrayOutputStream().apply {
            write(intBytes(8 + payload.size))
            write(type.toByteArray(Charsets.ISO_8859_1))
            write(payload)
        }.toByteArray()

    /** A `data` atom is version and type flags, four reserved locale bytes, then the value. */
    private fun dataPayload(value: ByteArray, wellKnownType: Int = 1): ByteArray =
        ByteArrayOutputStream().apply {
            write(intBytes(wellKnownType))
            write(intBytes(0))
            write(value)
        }.toByteArray()

    private fun dataAtom(text: String): ByteArray =
        atom("data", dataPayload(text.toByteArray(Charsets.UTF_8)))

    private fun intData(value: Int): ByteArray =
        atom("data", dataPayload(byteArrayOf(value.toByte()), wellKnownType = 21))

    private fun textTag(type: String, text: String): ByteArray = atom(type, dataAtom(text))

    private fun freeformTag(name: String, value: String): ByteArray =
        freeformTag(name, dataAtom(value))

    private fun freeformTag(name: String, dataAtom: ByteArray): ByteArray =
        atom(
            "----",
            ByteArrayOutputStream().apply {
                write(fullBoxAtom("mean", "com.apple.iTunes"))
                write(fullBoxAtom("name", name))
                write(dataAtom)
            }.toByteArray(),
        )

    /** `mean` and `name` carry four bytes of version and flags before their text. */
    private fun fullBoxAtom(type: String, text: String): ByteArray =
        atom(
            type,
            ByteArrayOutputStream().apply {
                write(intBytes(0))
                write(text.toByteArray(Charsets.UTF_8))
            }.toByteArray(),
        )

    private fun intBytes(value: Int): ByteArray = byteArrayOf(
        (value ushr 24).toByte(),
        (value ushr 16).toByte(),
        (value ushr 8).toByte(),
        value.toByte(),
    )

    private companion object {
        const val LONG_SYNOPSIS =
            "Enter the brutal and elite world of a war college for dragon riders. " +
                "Twenty-year-old Violet Sorrengail was supposed to enter the Scribe " +
                "Quadrant, living a quiet life among books and history."
    }
}
