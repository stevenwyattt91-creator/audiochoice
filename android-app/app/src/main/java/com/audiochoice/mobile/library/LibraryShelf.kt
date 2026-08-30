package com.audiochoice.mobile.library

import com.audiochoice.mobile.data.LibraryBook

/**
 * Which of the two library tabs a book belongs to.
 *
 * Two shelves rather than one combined list, because opening a book from each does something
 * different: an audiobook opens the player, an ebook opens the reader. A single list would
 * promise they behave alike.
 */
enum class LibraryShelf(val label: String) {
    AUDIOBOOKS("Audiobooks"),
    EBOOKS("Ebooks"),
}

/**
 * How a shelf is ordered. Shared between both shelves, which is the part of the
 * one-combined-list idea worth keeping: a listener learns the control once.
 */
enum class LibrarySortOrder(val label: String) {
    RECENT("Recently Added"),
    A_TO_Z("A–Z"),
    Z_TO_A("Z–A"),
    DURATION("Longest first"),
}

/**
 * Splits, filters and orders the library.
 *
 * Pure and separate from the composables so the rules can be tested without a device. These
 * decide what a listener sees on each tab, and getting the split wrong would hide a book they
 * own -- which looks identical to having lost it.
 */
object LibraryShelves {

    /**
     * The file type a narrated book carries.
     *
     * Matches `NarrationImporter.FILE_TYPE`. Compared case-insensitively and trimmed because
     * this value travels through the backend and back, and a shelf assignment is too important
     * to hinge on exact casing surviving that round trip.
     */
    const val EBOOK_FILE_TYPE = "epub"

    /**
     * Which shelf a book belongs on.
     *
     * An imported audiobook with an EPUB attached for read-along stays on the audiobook shelf,
     * and gets there for free: attaching an EPUB never changes the fingerprint, whose
     * `fileType` is still the audio format. That is why this reads the fingerprint rather than
     * asking whether an EPUB is present -- the latter would move a book between shelves the
     * moment a listener attached a reading edition to it.
     */
    fun shelfFor(book: LibraryBook): LibraryShelf =
        if (book.fingerprint.fileType.trim().equals(EBOOK_FILE_TYPE, ignoreCase = true)) {
            LibraryShelf.EBOOKS
        } else {
            LibraryShelf.AUDIOBOOKS
        }

    fun booksOn(books: List<LibraryBook>, shelf: LibraryShelf): List<LibraryBook> =
        books.filter { shelfFor(it) == shelf }

    /** Whether the ebook tab is worth showing at all. */
    fun hasEbooks(books: List<LibraryBook>): Boolean =
        books.any { shelfFor(it) == LibraryShelf.EBOOKS }

    /** Search across the fields a listener would type, matching the existing behaviour. */
    fun matching(books: List<LibraryBook>, query: String): List<LibraryBook> {
        if (query.isBlank()) return books
        return books.filter { book ->
            book.title.contains(query, ignoreCase = true) ||
                book.author?.contains(query, ignoreCase = true) == true
        }
    }

    /**
     * Orders one shelf.
     *
     * A book with no duration sorts after every book that has one, rather than being treated as
     * zero. A narrated book has no duration until its chapters are rendered, and sorting it as
     * the shortest book in the library would bury a book someone just imported at the bottom of
     * their own list.
     */
    fun ordered(books: List<LibraryBook>, order: LibrarySortOrder): List<LibraryBook> =
        when (order) {
            LibrarySortOrder.RECENT -> books.sortedByDescending { it.addedAt }
            LibrarySortOrder.A_TO_Z -> books.sortedBy { it.title.lowercase() }
            LibrarySortOrder.Z_TO_A -> books.sortedByDescending { it.title.lowercase() }
            LibrarySortOrder.DURATION -> books.sortedWith(
                compareBy<LibraryBook> { it.fingerprint.duration == null }
                    .thenByDescending { it.fingerprint.duration ?: 0.0 }
                    .thenBy { it.title.lowercase() },
            )
        }

    /** Filtered and ordered in one call, which is what a shelf actually renders. */
    fun visible(
        books: List<LibraryBook>,
        shelf: LibraryShelf,
        query: String,
        order: LibrarySortOrder,
    ): List<LibraryBook> = ordered(matching(booksOn(books, shelf), query), order)

    /**
     * The book to offer as "continue" on one shelf.
     *
     * Scoped to the shelf rather than the whole library, because a shelf offering to resume a
     * book that is not on it -- and that opens a different surface when tapped -- would be
     * actively confusing. Prefers a book already started and unfinished; otherwise the first
     * on the shelf, which is what the combined list did.
     */
    fun featuredOn(books: List<LibraryBook>, shelf: LibraryShelf): LibraryBook? {
        val shelfBooks = booksOn(books, shelf)
        return shelfBooks.firstOrNull { it.playbackPositionSeconds > 0 && !it.isFinished }
            ?: shelfBooks.firstOrNull()
    }

    /**
     * How far through a book to show, as a fraction.
     *
     * Returns null when there is no duration to measure against. A narrated book that is still
     * rendering has none, and a progress bar drawn against an unknown total would be showing a
     * proportion of nothing -- which reads as "barely started" no matter how far in they are.
     */
    fun progressOf(book: LibraryBook): Float? {
        val duration = book.fingerprint.duration ?: return null
        if (duration <= 0.0) return null
        return (book.playbackPositionSeconds / duration).coerceIn(0.0, 1.0).toFloat()
    }
}
