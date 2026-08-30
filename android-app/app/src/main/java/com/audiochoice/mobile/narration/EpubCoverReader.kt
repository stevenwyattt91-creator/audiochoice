package com.audiochoice.mobile.narration

import java.io.InputStream
import java.util.zip.ZipInputStream

/**
 * Pulls one named entry's bytes out of an EPUB.
 *
 * Lives in the narration package rather than as a method on `EpubTextReader`, which every
 * build's reader depends on. Nothing here is needed by the imported-audiobook path, and
 * adding to a shared reader for one caller's benefit is how a stable class starts drifting.
 *
 * A second pass over the archive is the deliberate cost. Holding cover bytes on
 * `EpubDocument` would attach a megabyte or two of image to a model that is otherwise text
 * and offsets, and that model is kept for the life of an import.
 */
object EpubCoverReader {

    /** Beyond this an entry is not a cover image, whatever the manifest called it. */
    const val MAXIMUM_COVER_BYTES = 12 * 1024 * 1024

    /**
     * The bytes of [entryName], or null when it is absent, empty or implausibly large.
     *
     * Never throws. A missing or unreadable cover leaves the default in place, which is a
     * complete outcome rather than a failure -- a book with no artwork still reads aloud.
     */
    fun readEntry(input: InputStream, entryName: String): ByteArray? {
        if (entryName.isBlank()) return null
        val wanted = normalize(entryName)
        return runCatching {
            ZipInputStream(input).use { zip ->
                var found: ByteArray? = null
                var entry = zip.nextEntry
                while (entry != null && found == null) {
                    if (!entry.isDirectory && normalize(entry.name) == wanted) {
                        // The declared size is a hint and can be -1 or a lie, so the cap is
                        // enforced while reading rather than trusted beforehand.
                        found = zip.readAtMost(MAXIMUM_COVER_BYTES)?.takeIf { it.isNotEmpty() }
                    }
                    if (found == null) entry = zip.nextEntry
                }
                found
            }
        }.getOrNull()
    }

    /**
     * Reads up to [limit] bytes, or null if the stream carries more.
     *
     * Returning null rather than a truncated array on purpose: half an image decodes to
     * nothing useful, and a partial cover would be stored as if it were the real one.
     */
    private fun InputStream.readAtMost(limit: Int): ByteArray? {
        val buffer = ByteArray(64 * 1024)
        val collected = java.io.ByteArrayOutputStream()
        while (true) {
            val count = read(buffer)
            if (count < 0) break
            if (collected.size() + count > limit) return null
            collected.write(buffer, 0, count)
        }
        return collected.toByteArray()
    }

    private fun normalize(path: String): String =
        path.replace('\\', '/').trimStart('/').lowercase(java.util.Locale.US)
}
