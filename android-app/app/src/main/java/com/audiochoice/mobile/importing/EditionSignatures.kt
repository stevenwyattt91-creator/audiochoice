package com.audiochoice.mobile.importing

import com.audiochoice.contracts.BookFingerprint
import com.audiochoice.contracts.EditionSignature
import com.audiochoice.mobile.data.AudioChapter
import kotlin.math.roundToInt

/**
 * Builds the identity evidence reported to the server for a local audio file.
 *
 * Extracted because two callers need it -- importing a file and opening a book
 * imported before signatures existed -- and because these are the decisions that
 * determine whether a converted copy is recognised as the same recording. Keeping
 * them here rather than inside a ViewModel is what makes them testable.
 */
object EditionSignatures {

    /**
     * @return the evidence worth sending, or null when the file states nothing
     *   useful. Reporting an empty signature would cost a request and tell the
     *   server nothing.
     */
    fun from(tags: AudioEditionTags, chapters: List<AudioChapter>): EditionSignature? {
        val signature = EditionSignature(
            productIdentifier = tags.productIdentifier?.takeIf { it.isNotBlank() },
            narrator = tags.narrator?.trim()?.takeIf { it.isNotBlank() },
            chapterOffsetSeconds = chapterOffsets(chapters),
        )
        val hasEvidence = signature.productIdentifier != null ||
            signature.narrator != null ||
            signature.chapterOffsetSeconds != null
        return signature.takeIf { hasEvidence }
    }

    /**
     * A lone chapter is just the whole file and distinguishes nothing, so it is not
     * reported. Offsets are whole seconds; the server allows a couple of seconds of
     * drift when comparing, which covers this rounding.
     */
    private fun chapterOffsets(chapters: List<AudioChapter>): List<Int>? = chapters
        .takeIf { it.size > 1 }
        ?.map { chapter -> chapter.startSeconds.coerceAtLeast(0.0).roundToInt() }

    /**
     * The fingerprint of the file on this device, when the library row is being saved
     * under a different one.
     *
     * A library row deliberately adopts a canonical edition's fingerprint so that a
     * converted file does not create a duplicate entry. That leaves the scan and its
     * transcript filed under the local file's hash instead, so the divergence has to
     * be reported or the two cannot be reconnected.
     *
     * @return null when both describe the same file and there is nothing to link.
     */
    fun sourceFingerprintFor(
        localFile: BookFingerprint,
        libraryRow: BookFingerprint,
    ): BookFingerprint? = localFile.takeIf { source ->
        // Mirrors the server's identity key, which is version, hash and size only.
        source.version != libraryRow.version ||
            source.fileSize != libraryRow.fileSize ||
            !source.sha256.equals(libraryRow.sha256, ignoreCase = true)
    }
}
