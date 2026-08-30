package com.audiochoice.mobile.reader

import com.audiochoice.mobile.data.SourceRange

/**
 * Where extraction placed one spine document inside Book_Text.
 *
 * Needed because a navigation entry points at an archive entry, not at an
 * offset, so turning "chapter3.xhtml" into a chapter boundary requires knowing
 * where that document landed.
 */
data class ResourceSpan(val entryName: String, val range: SourceRange)

/** Where a chapter list came from, recorded so a spine fallback is visible. */
enum class NavigationSource { EPUB3_NAV, NCX, SPINE_FALLBACK }

/**
 * One top-level entry of a book's table of contents.
 *
 * [targetFragment] is the part after `#`, present when a chapter begins partway
 * through a spine document. Two chapters sharing one document is common in
 * single-file EPUBs, and without the fragment they would collapse into one.
 */
data class NavigationEntry(
    val title: String?,
    val targetEntry: String,
    val targetFragment: String? = null,
)

data class NavigationOutline(
    val source: NavigationSource,
    val entries: List<NavigationEntry>,
)

/** An encrypted archive entry that carries the book's text. */
data class EncryptedTextResource(val entryName: String, val role: TextResourceRole)

/**
 * Why extraction produced nothing at all.
 *
 * The two cases are separate because they are separate decline reasons for the
 * listener: one means the file could not be opened, the other means it opened
 * fine and is not an EPUB this app can read. Collapsing them would tell someone
 * with a corrupt download the same thing as someone who picked a PDF.
 */
enum class ExtractionFailure {
    /** The stream would not open, or the ZIP central directory would not read. */
    UNREADABLE_ARCHIVE,

    /** No `META-INF/container.xml`, or it names a package document that is absent. */
    MISSING_PACKAGE_DOCUMENT,
}

/** Which text-bearing role an archive entry plays. */
enum class TextResourceRole { PACKAGE_DOCUMENT, NAVIGATION_DOCUMENT, NCX_DOCUMENT, SPINE_DOCUMENT }

/**
 * Everything narration needs from a Source_EPUB, produced by one pass over the
 * archive.
 *
 * [text] is Book_Text: the flat string every character offset in the feature
 * indexes. It is byte-for-byte stable for a given file and a given
 * [extractionVersion], which is what lets a persisted plan be trusted or
 * discarded rather than silently reinterpreted.
 *
 * A document with a non-empty [storeDrmResources] has unreadable text and must be
 * declined. [text] is empty in that case: extraction stops before converting a
 * spine document, so there is nothing extracted to purge.
 */
data class EpubDocument(
    val text: String,
    val extractionVersion: Int,
    val language: String?,
    val title: String?,
    val author: String?,
    val coverImageEntry: String?,
    /** Spine documents in spine order, with where each landed in [text]. */
    val resources: List<ResourceSpan>,
    /** Regions unsuited to narration, already extended over descendants. */
    val nonProseRanges: List<SourceRange>,
    /** Offsets of `id` attributes, keyed `"entryName#id"`, for chapter anchors. */
    val anchorOffsets: Map<String, Int>,
    val navigation: NavigationOutline?,
    /**
     * Whether the package document declared a contents source at all, whichever
     * kind.
     *
     * Separate from [navigation] because "declared but unparseable" and "never
     * declared" call for different handling: the first is a degraded book whose
     * chapter list came from a fallback and should say so, the second is an
     * ordinary book that simply has no contents list.
     */
    val declaresNavigation: Boolean = false,
    /** Every entry named by a `CipherReference`, whatever its role. */
    val encryptedEntries: Set<String>,
    /** Encrypted entries that carry text. Non-empty means store DRM. */
    val storeDrmResources: List<EncryptedTextResource>,
    /** Spine documents absent from the archive, encrypted, or unparseable. */
    val unreadableSpineEntries: List<String>,
    /** Spine documents the package document listed, before readability filtering. */
    val declaredSpineEntries: List<String>,
    /** Set when extraction produced nothing at all. */
    val failure: ExtractionFailure? = null,
) {
    val carriesStoreDrm: Boolean get() = storeDrmResources.isNotEmpty()

    /** Letters and digits only, which is what the minimum-length check counts. */
    val letterOrDigitCount: Int get() = text.count { it.isLetterOrDigit() }

    companion object {
        /** Extraction produced nothing. [failure] says which of the two cases it was. */
        fun failed(failure: ExtractionFailure): EpubDocument = EpubDocument(
            text = "",
            extractionVersion = EpubTextReader.NARRATION_EXTRACTION_VERSION,
            language = null,
            title = null,
            author = null,
            coverImageEntry = null,
            resources = emptyList(),
            nonProseRanges = emptyList(),
            anchorOffsets = emptyMap(),
            navigation = null,
            encryptedEntries = emptySet(),
            storeDrmResources = emptyList(),
            unreadableSpineEntries = emptyList(),
            declaredSpineEntries = emptyList(),
            failure = failure,
        )
    }
}
