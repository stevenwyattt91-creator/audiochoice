package com.audiochoice.mobile.narration

import com.audiochoice.contracts.BookFingerprint
import com.audiochoice.mobile.data.LibraryBook
import com.audiochoice.mobile.data.LibraryBookUpsertRequest
import com.audiochoice.mobile.reader.EpubDocument
import java.io.InputStream
import java.security.MessageDigest

/**
 * The file the listener chose, described without reference to Android.
 *
 * [openStream] is a factory rather than a stream because the import reads the file twice:
 * once to fingerprint it, once to extract its text. A single stream cannot be rewound, and
 * buffering 50 MB to make it rewindable would trade a cheap second read for a real risk of
 * running the device out of memory.
 */
class NarrationImportSource(
    val displayName: String?,
    val declaredSize: Long,
    val openStream: () -> InputStream?,
)

/** Identity of an imported EPUB, measured rather than declared. */
data class NarrationSourceFingerprint(val sha256: String, val fileSize: Long)

/** How an import ended. */
sealed interface NarrationImportOutcome {

    /**
     * The book is in the library and its text is on the device, ready to be scanned,
     * planned and rendered.
     */
    data class Imported(
        val fingerprint: BookFingerprint,
        val title: String,
        val author: String?,
        /**
         * True when the title came from the filename or the hash rather than the book.
         *
         * Recorded rather than hidden, because a derived title is the reason a book might
         * later be renamed, and it is also what keeps it out of any shared catalogue.
         */
        val titleWasDerived: Boolean,
        val document: EpubDocument,
        val coverStored: Boolean,
        /**
         * The library row the server saved.
         *
         * Carried out of the import rather than discarded because the screen that started it has
         * to publish it: the library list reloads off the imported book, and without it the
         * listener is shown a cached list that does not contain the book they just added. That was
         * a real bug -- the ebook tab never appeared, because as far as the library screen knew
         * there were no ebooks.
         */
        val libraryBook: LibraryBook,
    ) : NarrationImportOutcome

    /**
     * This exact file is already in the library.
     *
     * The persisted location is refreshed and nothing else is touched: the plan, the audio
     * already rendered and the listener's position all survive. Re-importing a book after
     * moving it in a file manager is the ordinary way to reach this, and losing hours of
     * synthesised audio for it would be indefensible.
     */
    data class AlreadyInLibrary(val sha256: String) : NarrationImportOutcome

    /** The file is not something this feature can read aloud. Carries listener-facing copy. */
    data class Declined(val reason: DeclineReason, val message: DeclineMessage) :
        NarrationImportOutcome

    /**
     * The persistable read permission could not be taken.
     *
     * No book is created and no plan is written. Without that permission the file is
     * readable now and unreadable after a restart, so importing would produce a library
     * entry that breaks silently later.
     */
    data object PermissionRefused : NarrationImportOutcome

    /** Something failed that is worth retrying, such as the library call. */
    data class Failed(val message: String) : NarrationImportOutcome
}

/**
 * Turns an accepted EPUB into an ordinary library book.
 *
 * Collaborators arrive as function types, matching the rest of the narration code, so the
 * ordering below can be tested without a `ContentResolver`, a network or a device. The
 * ordering is the part worth protecting: permission before reading, fingerprint before the
 * duplicate check, validation before anything is written, and the library row before the
 * scan that needs it.
 */
class NarrationImporter(
    private val store: NarrationStore,
    /** Must succeed before the file is read, or the import is abandoned. */
    private val takePersistablePermission: suspend () -> Boolean,
    private val readDocument: suspend (InputStream) -> EpubDocument,
    private val isAlreadyInLibrary: suspend (String) -> Boolean,
    /** Records where the file lives, for a fresh import and for a repeat alike. */
    private val persistSourceLocation: suspend (String) -> Unit,
    private val saveLibraryBook: suspend (LibraryBookUpsertRequest) -> LibraryBook,
    /**
     * Records the library row on the device.
     *
     * The device is the authority for a narrated book. Everything else about one is already local, and
     * making its place in the library the single exception is what allowed a book sitting on the phone
     * to become unreachable through it.
     */
    private val saveLocalLibraryBook: suspend (LibraryBook) -> Unit,
    private val saveCover: suspend (String, ByteArray) -> Unit,
) {

    suspend fun import(source: NarrationImportSource): NarrationImportOutcome {
        // First, because everything after it depends on the file still being readable after
        // a restart. Taking it later would mean discovering the refusal once a library row
        // already existed.
        if (!takePersistablePermission()) return NarrationImportOutcome.PermissionRefused

        val identity = fingerprint(source)
            ?: return NarrationImportOutcome.Failed(
                "That file could not be opened for reading.",
            )

        // Before the archive is decoded, because a book already imported needs none of that
        // work -- and must not have its artifacts disturbed by repeating it.
        if (isAlreadyInLibrary(identity.sha256)) {
            persistSourceLocation(identity.sha256)
            return NarrationImportOutcome.AlreadyInLibrary(identity.sha256)
        }

        val document = source.openStream()?.use { readDocument(it) }
            ?: return NarrationImportOutcome.Failed(
                "That file could not be opened for reading.",
            )

        when (val validation = EpubValidator.classify(document)) {
            is EpubValidation.Declined -> {
                // Nothing has been written for this book yet, but a previous attempt may
                // have left a directory behind. A declined book keeps no text on the device.
                store.deleteBook(identity.sha256)
                return NarrationImportOutcome.Declined(
                    validation.reason,
                    DeclineMessages.forReason(validation.reason),
                )
            }
            is EpubValidation.Accepted -> Unit
        }

        val derivedTitle = titleFor(document, source.displayName, identity.sha256)
        val fingerprint = BookFingerprint(
            version = 1,
            sha256 = identity.sha256,
            fileSize = identity.fileSize,
            // A narrated book has no runtime until its chapters are rendered, and inventing
            // one would put a wrong duration on every library and progress surface.
            duration = null,
            fileType = FILE_TYPE,
            workTitle = derivedTitle.title,
            author = derivedTitle.author,
        )

        val coverStored = storeCover(source, document, identity.sha256)

        val saved = runCatching {
            saveLibraryBook(
                LibraryBookUpsertRequest(
                    fingerprint = fingerprint,
                    title = derivedTitle.title,
                    author = derivedTitle.author,
                ),
            )
        }
        // The import no longer fails because the server did. A narrated book's file, its text and its
        // audio are all on the device; refusing the import over an unreachable library row would deny
        // a listener a book they already have, and keeping that row only on the server is what let one
        // disappear from the shelf with no way to bring it back.
        //
        // The server's copy is preferred when it answers, because it carries the identifier and the
        // reading position that sync between a listener's devices.
        val libraryBook = saved.getOrNull() ?: LibraryBook(
            // The hash, so the row is stable and recognisable as the same book when a server copy
            // eventually arrives and replaces it.
            id = identity.sha256,
            fingerprint = fingerprint,
            title = derivedTitle.title,
            author = derivedTitle.author,
            addedAt = timestamp(),
            updatedAt = timestamp(),
        )
        saveLocalLibraryBook(libraryBook)
        persistSourceLocation(identity.sha256)
        store.saveBookText(identity.sha256, document.text)

        return NarrationImportOutcome.Imported(
            fingerprint = fingerprint,
            title = derivedTitle.title,
            author = derivedTitle.author,
            titleWasDerived = derivedTitle.wasDerived,
            document = document,
            coverStored = coverStored,
            libraryBook = libraryBook,
        )
    }

    /** Stores the manifest cover, or leaves the default in place. */
    /** ISO-8601 UTC, matching the shape the server sends back so the two sort together. */
    private fun timestamp(): String =
        java.time.format.DateTimeFormatter.ISO_INSTANT.format(java.time.Instant.now())

    private suspend fun storeCover(
        source: NarrationImportSource,
        document: EpubDocument,
        sha256: String,
    ): Boolean {
        val entry = document.coverImageEntry?.takeIf { it.isNotBlank() } ?: return false
        val bytes = source.openStream()?.use { EpubCoverReader.readEntry(it, entry) }
            ?: return false
        return runCatching { saveCover(sha256, bytes); true }.getOrDefault(false)
    }

    /**
     * Streams the file once, producing both the digest and the true byte count.
     *
     * The count is measured rather than taken from the provider, which reports `-1` for many
     * sources and can be stale for others. Since the byte count is part of the fingerprint,
     * a wrong one would give the same book two identities and defeat the duplicate check
     * that protects already-rendered audio.
     */
    private fun fingerprint(source: NarrationImportSource): NarrationSourceFingerprint? =
        runCatching {
            val digest = MessageDigest.getInstance("SHA-256")
            var measured = 0L
            source.openStream()?.use { input ->
                val buffer = ByteArray(BUFFER_BYTES)
                while (true) {
                    val count = input.read(buffer)
                    if (count < 0) break
                    digest.update(buffer, 0, count)
                    measured += count
                }
            } ?: return null

            if (measured <= 0L) return null
            NarrationSourceFingerprint(
                // Lowercase, matching the on-device narration layout and the database's
                // `lower(sha256)` lookups. The audiobook inspector happens to produce
                // uppercase, and comparisons across the two are case-insensitive, but the
                // directory a book's audio lives in is not.
                sha256 = digest.digest().joinToString("") { "%02x".format(it) },
                fileSize = measured,
            )
        }.getOrNull()

    /** A title, an author, and whether the title had to be invented. */
    data class DerivedTitle(val title: String, val author: String?, val wasDerived: Boolean)

    companion object {
        const val FILE_TYPE = "epub"

        /** Long enough for a real title with a subtitle; short enough to store. */
        const val MAXIMUM_FIELD_CHARACTERS = 500

        private const val BUFFER_BYTES = 1024 * 1024

        /**
         * The title fallback chain, and the author alongside it.
         *
         * Three steps, each a worse name than the last but each still a name. The last one
         * exists because an import must not fail for want of a title: a book the listener
         * can see and rename is strictly better than a refused import, and the hash prefix
         * at least identifies the file uniquely.
         *
         * A derived title is flagged, and that flag is why a narrated book never reaches a
         * shared catalogue on the strength of a filename.
         */
        fun titleFor(
            document: EpubDocument,
            displayName: String?,
            sha256: String,
        ): DerivedTitle {
            val author = document.author?.let(::truncate)?.takeIf { it.isNotBlank() }

            document.title?.let(::truncate)?.takeIf { it.isNotBlank() }?.let { title ->
                return DerivedTitle(title, author, wasDerived = false)
            }

            val fromFilename = displayName
                ?.substringAfterLast('/')
                ?.removeSuffix(".epub")
                ?.removeSuffix(".EPUB")
                ?.trim()
                ?.let(::truncate)
                ?.takeIf { it.isNotBlank() }
            if (fromFilename != null) return DerivedTitle(fromFilename, author, wasDerived = true)

            return DerivedTitle(
                "Imported book ${sha256.take(8).lowercase()}",
                author,
                wasDerived = true,
            )
        }

        private fun truncate(value: String): String =
            value.trim().take(MAXIMUM_FIELD_CHARACTERS)
    }
}
