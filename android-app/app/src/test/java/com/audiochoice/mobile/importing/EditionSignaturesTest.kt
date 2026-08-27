package com.audiochoice.mobile.importing

import com.audiochoice.contracts.BookFingerprint
import com.audiochoice.mobile.data.AudioChapter
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * The evidence reported to the server, and the source-file link.
 *
 * These decide whether a converted copy of an audiobook is recognised as the same
 * recording, which is what keeps its transcript and filters reachable.
 */
class EditionSignaturesTest {

    private fun chapter(startSeconds: Double) = AudioChapter("Chapter", startSeconds, startSeconds + 10)

    @Test
    fun `reports a product identifier and narrator`() {
        val signature = EditionSignatures.from(
            AudioEditionTags(asin = "B0CTJ1PDKM", narrator = "Zachary Quinto"),
            emptyList(),
        )
        assertEquals("B0CTJ1PDKM", signature?.productIdentifier)
        assertEquals("Zachary Quinto", signature?.narrator)
    }

    @Test
    fun `reports chapter offsets in whole seconds`() {
        val signature = EditionSignatures.from(
            AudioEditionTags(narrator = "Someone"),
            listOf(chapter(0.0), chapter(1200.4), chapter(2400.6)),
        )
        assertEquals(listOf(0, 1200, 2401), signature?.chapterOffsetSeconds)
    }

    /** One chapter is the whole file and separates nothing, so it is not evidence. */
    @Test
    fun `does not report a single chapter`() {
        val signature = EditionSignatures.from(AudioEditionTags(narrator = "Someone"), listOf(chapter(0.0)))
        assertNull(signature?.chapterOffsetSeconds)
    }

    /** Sending an empty signature would cost a request and say nothing. */
    @Test
    fun `returns null when the file states nothing useful`() {
        assertNull(EditionSignatures.from(AudioEditionTags(), emptyList()))
        assertNull(EditionSignatures.from(AudioEditionTags(title = "King Sorrow"), emptyList()))
        assertNull(EditionSignatures.from(AudioEditionTags(narrator = "   "), emptyList()))
    }

    @Test
    fun `chapters alone are enough to be worth reporting`() {
        val signature = EditionSignatures.from(
            AudioEditionTags(),
            listOf(chapter(0.0), chapter(600.0)),
        )
        assertEquals(listOf(0, 600), signature?.chapterOffsetSeconds)
    }

    private val localFile = BookFingerprint(
        version = 1,
        sha256 = "AA".repeat(32),
        fileSize = 1_000,
        fileType = "m4b",
    )

    @Test
    fun `reports no source link when the library row is the same file`() {
        assertNull(EditionSignatures.sourceFingerprintFor(localFile, localFile))
    }

    /** Hashes are compared case-insensitively; the server lowercases its key. */
    @Test
    fun `casing alone is not a divergence`() {
        val lowercased = localFile.copy(sha256 = localFile.sha256.lowercase())
        assertNull(EditionSignatures.sourceFingerprintFor(localFile, lowercased))
    }

    @Test
    fun `reports the local file when the row adopts another fingerprint`() {
        val canonical = localFile.copy(sha256 = "BB".repeat(32))
        assertEquals(localFile, EditionSignatures.sourceFingerprintFor(localFile, canonical))
    }

    /**
     * Size and version are part of the server's identity key, so a difference in
     * either is a different edition record even when the hash matches.
     */
    @Test
    fun `a differing size or version counts as a divergence`() {
        assertEquals(
            localFile,
            EditionSignatures.sourceFingerprintFor(localFile, localFile.copy(fileSize = 2_000)),
        )
        assertEquals(
            localFile,
            EditionSignatures.sourceFingerprintFor(localFile, localFile.copy(version = 2)),
        )
    }

    /**
     * Metadata is copied onto a canonical fingerprint without changing the identity
     * key, and that must not be mistaken for a different file.
     */
    @Test
    fun `metadata differences alone are not a divergence`() {
        val retitled = localFile.copy(workTitle = "Fourth Wing", author = "Rebecca Yarros")
        assertNull(EditionSignatures.sourceFingerprintFor(localFile, retitled))
    }
}
