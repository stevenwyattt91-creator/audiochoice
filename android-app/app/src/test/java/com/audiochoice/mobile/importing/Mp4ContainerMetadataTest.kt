package com.audiochoice.mobile.importing

import java.io.ByteArrayOutputStream
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Runtime and cover art, read from the container itself.
 *
 * Both used to come only from MediaMetadataRetriever, which needs a seekable data source and
 * returns nothing when it cannot open one. A real import showed the consequence: title, author,
 * narrator and chapters all arrived, because those are parsed from the atoms directly, while
 * runtime and cover were simply absent. These read from the same place the working fields do.
 */
class Mp4ContainerMetadataTest {

    private fun atom(type: String, body: ByteArray): ByteArray {
        val output = ByteArrayOutputStream()
        val size = body.size + 8
        output.write(byteArrayOf(
            (size ushr 24).toByte(), (size ushr 16).toByte(),
            (size ushr 8).toByte(), size.toByte(),
        ))
        // Latin-1, not ASCII: the iTunes atom types begin with byte 0xA9, which ASCII
        // cannot represent and silently replaces with a question mark.
        output.write(type.toByteArray(Charsets.ISO_8859_1))
        output.write(body)
        return output.toByteArray()
    }

    private fun uint32(value: Long): ByteArray = byteArrayOf(
        (value ushr 24).toByte(), (value ushr 16).toByte(),
        (value ushr 8).toByte(), value.toByte(),
    )

    /** A version 0 movie header: 32-bit times, timescale then duration. */
    private fun movieHeaderV0(timescale: Long, duration: Long): ByteArray {
        val output = ByteArrayOutputStream()
        output.write(byteArrayOf(0, 0, 0, 0))       // version 0 and flags
        output.write(uint32(0))                      // creation time
        output.write(uint32(0))                      // modification time
        output.write(uint32(timescale))
        output.write(uint32(duration))
        output.write(ByteArray(80))                  // the rest of the header
        return output.toByteArray()
    }

    /** A version 1 movie header: 64-bit times, so timescale and duration move. */
    private fun movieHeaderV1(timescale: Long, duration: Long): ByteArray {
        val output = ByteArrayOutputStream()
        output.write(byteArrayOf(1, 0, 0, 0))        // version 1 and flags
        output.write(ByteArray(8))                   // creation time
        output.write(ByteArray(8))                   // modification time
        output.write(uint32(timescale))
        output.write(ByteArray(4))                   // duration, high 32 bits
        output.write(uint32(duration))               // duration, low 32 bits
        output.write(ByteArray(80))
        return output.toByteArray()
    }

    private fun dataAtom(typeFlags: Long, body: ByteArray): ByteArray {
        val output = ByteArrayOutputStream()
        output.write(uint32(typeFlags))
        output.write(ByteArray(4))                   // reserved locale
        output.write(body)
        return atom("data", output.toByteArray())
    }

    @Test
    fun `a version 0 movie header gives the runtime in seconds`() {
        // Ten hours at a millisecond timescale, a common audiobook shape.
        val header = movieHeaderV0(timescale = 1000, duration = 36_000_000)
        assertEquals(36_000.0, Mp4TagParser.durationSeconds(header)!!, 0.001)
    }

    @Test
    fun `a version 1 movie header gives the runtime in seconds`() {
        val header = movieHeaderV1(timescale = 1000, duration = 36_000_000)
        assertEquals(36_000.0, Mp4TagParser.durationSeconds(header)!!, 0.001)
    }

    @Test
    fun `a non-millisecond timescale is honoured`() {
        // 44.1 kHz timescales appear in files written straight from audio.
        val header = movieHeaderV0(timescale = 44_100, duration = 44_100L * 7_200)
        assertEquals(7_200.0, Mp4TagParser.durationSeconds(header)!!, 0.001)
    }

    @Test
    fun `a header without a duration reports nothing rather than zero`() {
        // Some converters leave this at zero. Reporting it would show the book as
        // instantaneous, which reads as a corrupt import rather than a missing field.
        assertNull(Mp4TagParser.durationSeconds(movieHeaderV0(timescale = 1000, duration = 0)))
    }

    @Test
    fun `a header with no timescale reports nothing rather than dividing by zero`() {
        assertNull(Mp4TagParser.durationSeconds(movieHeaderV0(timescale = 0, duration = 1000)))
    }

    @Test
    fun `a truncated header is refused`() {
        assertNull(Mp4TagParser.durationSeconds(byteArrayOf(0, 0, 0, 0, 1, 2)))
        assertNull(Mp4TagParser.durationSeconds(ByteArray(0)))
    }

    @Test
    fun `cover art is read from the metadata list`() {
        // 13 is the well-known type for JPEG. The bytes only have to be long enough to be
        // plausible; nothing here decodes the image.
        val image = ByteArray(512) { (it % 251).toByte() }
        val ilst = atom("covr", dataAtom(typeFlags = 13, body = image))
        assertArrayEquals(image, Mp4TagParser.coverArt(ilst))
    }

    @Test
    fun `cover art is found alongside the other tags`() {
        val image = ByteArray(300) { 7 }
        val ilst = atom("\u00A9nam", dataAtom(1, "The Deal".toByteArray(Charsets.UTF_8))) +
            atom("covr", dataAtom(14, image)) +
            atom("\u00A9ART", dataAtom(1, "Elle Kennedy".toByteArray(Charsets.UTF_8)))

        // The point of the fix: the fields that already worked and the ones that did not now
        // come from the same pass over the same bytes.
        val tags = Mp4TagParser.parseIlst(ilst)
        assertEquals("The Deal", tags.title)
        assertEquals("Elle Kennedy", tags.author)
        assertArrayEquals(image, Mp4TagParser.coverArt(ilst))
    }

    @Test
    fun `a metadata list with no artwork reports none`() {
        val ilst = atom("\u00A9nam", dataAtom(1, "No Cover".toByteArray(Charsets.UTF_8)))
        assertNull(Mp4TagParser.coverArt(ilst))
    }

    @Test
    fun `an implausibly small artwork payload is refused`() {
        // Guards against treating a stray or padded atom as an image.
        val ilst = atom("covr", dataAtom(13, ByteArray(8)))
        assertNull(Mp4TagParser.coverArt(ilst))
    }

    @Test
    fun `a corrupt metadata list yields no artwork rather than throwing`() {
        // Imports are untrusted input: a truncated or hostile container must produce fewer
        // fields, never an exception during import.
        val truncated = byteArrayOf(0, 0, 0, 40, 'c'.code.toByte(), 'o'.code.toByte(),
            'v'.code.toByte(), 'r'.code.toByte(), 0, 0)
        assertNull(Mp4TagParser.coverArt(truncated))
        assertTrue(Mp4TagParser.parseIlst(truncated).title == null)
    }
}
