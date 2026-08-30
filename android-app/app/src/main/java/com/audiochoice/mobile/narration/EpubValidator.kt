package com.audiochoice.mobile.narration

import android.content.ContentResolver
import android.net.Uri
import com.audiochoice.mobile.reader.EpubDocument
import com.audiochoice.mobile.reader.EpubTextReader
import com.audiochoice.mobile.reader.ExtractionFailure
import com.audiochoice.mobile.reader.TextResourceRole

/**
 * Decides whether a selected file can be narrated, and says exactly one thing
 * about it when it cannot.
 *
 * One reason, not a list. A listener who picked a DRM-protected file that also
 * happens to be short does not need to hear about the length; they need to hear
 * the one fact that explains the outcome and points somewhere useful. The order
 * runs from "nothing could be read" outward to "the text is too short", so the
 * reason reported is always the earliest thing that went wrong.
 */
object EpubValidator {

    /** Book_Text must carry at least this many letters or digits to be worth narrating. */
    const val MINIMUM_LETTERS_OR_DIGITS = 500

    /**
     * Read and classify a selected file.
     *
     * Runs off the main thread through [EpubTextReader.readNarrationDocument],
     * which unzips and decodes the archive.
     */
    suspend fun validate(resolver: ContentResolver, uri: Uri): EpubValidation =
        classify(EpubTextReader.readNarrationDocument(resolver, uri))

    /**
     * The decision itself, over an already-extracted document.
     *
     * Separated so the ordering can be exercised against real archives without a
     * `ContentResolver`. The ordering is the part most likely to regress, because
     * it is the part a later change is most likely to reorder for convenience.
     */
    internal fun classify(document: EpubDocument): EpubValidation {
        document.failure?.let { failure ->
            return EpubValidation.Declined(
                when (failure) {
                    ExtractionFailure.UNREADABLE_ARCHIVE -> DeclineReason.CouldNotOpen
                    ExtractionFailure.MISSING_PACKAGE_DOCUMENT -> DeclineReason.NotAnEpub
                },
            )
        }

        if (document.carriesStoreDrm) {
            return EpubValidation.Declined(
                DeclineReason.StoreDrm(
                    encryptedRoles = document.storeDrmResources.map { it.role }.distinct(),
                    encryptedEntries = document.storeDrmResources.map { it.entryName },
                ),
            )
        }

        // Every spine document absent, encrypted or unparseable. Distinct from
        // Store_DRM: the file is not protected, its text simply is not there.
        if (document.resources.isEmpty()) {
            return EpubValidation.Declined(DeclineReason.TextUnreadable)
        }

        if (document.letterOrDigitCount < MINIMUM_LETTERS_OR_DIGITS) {
            return EpubValidation.Declined(
                DeclineReason.TooLittleText(
                    letterOrDigitCount = document.letterOrDigitCount,
                    minimum = MINIMUM_LETTERS_OR_DIGITS,
                ),
            )
        }

        return EpubValidation.Accepted(document)
    }
}

/**
 * The outcome of validating a selected file.
 *
 * [Declined] deliberately carries no [EpubDocument]. That is the structural
 * guarantee behind "retain nothing on a decline": there is no field through which
 * a caller could reach extracted text for a rejected file, so the rule cannot be
 * broken by forgetting to clear something. Extraction holds the text in local
 * scope and drops it, and for store DRM it stops before converting a single spine
 * document, so there was never anything to purge.
 */
sealed interface EpubValidation {
    data class Accepted(val document: EpubDocument) : EpubValidation

    data class Declined(val reason: DeclineReason) : EpubValidation
}

/**
 * Why a file cannot be narrated.
 *
 * Modelled rather than stringly-typed so the decline surface is a function of the
 * reason, and so the ordering can be asserted in a test instead of being implied
 * by whichever message happened to be produced.
 */
sealed interface DeclineReason {

    /** The stream would not open, or the archive is not a readable ZIP. */
    data object CouldNotOpen : DeclineReason

    /** It opened, but it is not an EPUB with a package document. */
    data object NotAnEpub : DeclineReason

    /**
     * The store that sold the file encrypted its text.
     *
     * Carries which text-bearing roles are encrypted so the decline can name them,
     * because "your file is protected" without saying what is protected reads as
     * the app guessing.
     */
    data class StoreDrm(
        val encryptedRoles: List<TextResourceRole>,
        val encryptedEntries: List<String>,
    ) : DeclineReason

    /** Not protected, but no spine document could be read. */
    data object TextUnreadable : DeclineReason

    data class TooLittleText(val letterOrDigitCount: Int, val minimum: Int) : DeclineReason
}
