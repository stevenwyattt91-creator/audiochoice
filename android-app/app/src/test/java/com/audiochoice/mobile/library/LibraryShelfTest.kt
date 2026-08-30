package com.audiochoice.mobile.library

import com.audiochoice.contracts.BookFingerprint
import com.audiochoice.mobile.data.LibraryBook
import com.audiochoice.mobile.narration.NarrationConfig
import com.audiochoice.mobile.narration.NarrationImporter
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class LibraryShelfTest {

    // region the split

    /**
     * The split reads the fingerprint's file type, which is what the narration importer stamps.
     * Pinned against that constant rather than a literal, so renaming one cannot silently empty
     * a shelf.
     */
    @Test
    fun `the ebook shelf matches what the importer stamps`() {
        assertEquals(NarrationImporter.FILE_TYPE, LibraryShelves.EBOOK_FILE_TYPE)
        assertEquals(
            LibraryShelf.EBOOKS,
            LibraryShelves.shelfFor(book("e", fileType = NarrationImporter.FILE_TYPE)),
        )
    }

    @Test
    fun `audio formats land on the audiobook shelf`() {
        listOf("m4b", "mp3", "m4a", "aax", "M4B", "flac").forEach { format ->
            assertEquals(
                "$format should be an audiobook",
                LibraryShelf.AUDIOBOOKS,
                LibraryShelves.shelfFor(book("a", fileType = format)),
            )
        }
    }

    /**
     * The file type travels to the backend and back, so a shelf assignment must not hinge on
     * casing or stray whitespace surviving that round trip.
     */
    @Test
    fun `the ebook type is matched case-insensitively and trimmed`() {
        listOf("epub", "EPUB", "ePub", " epub ", "epub\n").forEach { value ->
            assertEquals(
                "'$value' should be an ebook",
                LibraryShelf.EBOOKS,
                LibraryShelves.shelfFor(book("e", fileType = value)),
            )
        }
    }

    /**
     * The case the design turns on. An audiobook with an EPUB attached for read-along stays on
     * the audiobook shelf, and never appears on both.
     *
     * It gets there for free because attaching an EPUB never changes the fingerprint. Asserted
     * anyway, because the tempting implementation -- "does this book have an EPUB?" -- would
     * move a book between shelves the moment someone attached a reading edition.
     */
    @Test
    fun `an audiobook with an attached epub stays on the audiobook shelf`() {
        val audiobookWithReadAlong = book("a", fileType = "m4b", duration = 3_600.0)

        assertEquals(LibraryShelf.AUDIOBOOKS, LibraryShelves.shelfFor(audiobookWithReadAlong))

        val shelves = listOf(LibraryShelf.AUDIOBOOKS, LibraryShelf.EBOOKS).map { shelf ->
            shelf to LibraryShelves.booksOn(listOf(audiobookWithReadAlong), shelf)
        }.toMap()
        assertEquals(1, shelves[LibraryShelf.AUDIOBOOKS]?.size)
        assertTrue(
            "an audiobook appeared on the ebook shelf too",
            shelves[LibraryShelf.EBOOKS].isNullOrEmpty(),
        )
    }

    /** Every book lands on exactly one shelf. No book may be hidden or duplicated. */
    @Test
    fun `every book appears on exactly one shelf`() {
        val books = listOf(
            book("1", fileType = "m4b"),
            book("2", fileType = "epub"),
            book("3", fileType = "mp3"),
            book("4", fileType = "epub"),
            book("5", fileType = "octet-stream"),
        )

        val audiobooks = LibraryShelves.booksOn(books, LibraryShelf.AUDIOBOOKS)
        val ebooks = LibraryShelves.booksOn(books, LibraryShelf.EBOOKS)

        assertEquals(books.size, audiobooks.size + ebooks.size)
        assertTrue(
            "a book is on both shelves",
            audiobooks.map { it.id }.intersect(ebooks.map { it.id }.toSet()).isEmpty(),
        )
        assertEquals(setOf("2", "4"), ebooks.map { it.id }.toSet())
    }

    @Test
    fun `the ebook tab is only worth showing when there are ebooks`() {
        assertFalse(LibraryShelves.hasEbooks(listOf(book("a", fileType = "m4b"))))
        assertFalse(LibraryShelves.hasEbooks(emptyList()))
        assertTrue(LibraryShelves.hasEbooks(listOf(book("e", fileType = "epub"))))
    }

    // endregion

    // region ordering

    /**
     * A narrated book has no duration until its chapters are rendered. Treating that as zero
     * would bury a book someone just imported at the bottom of their own list.
     */
    @Test
    fun `a book with no duration sorts after every book that has one`() {
        val books = listOf(
            book("none", fileType = "epub", duration = null, title = "No duration"),
            book("short", fileType = "m4b", duration = 600.0, title = "Short"),
            book("long", fileType = "m4b", duration = 36_000.0, title = "Long"),
        )

        val ordered = LibraryShelves.ordered(books, LibrarySortOrder.DURATION)

        assertEquals(listOf("long", "short", "none"), ordered.map { it.id })
    }

    /** Several books with no duration keep a stable, name-ordered arrangement among themselves. */
    @Test
    fun `books with no duration are ordered by title among themselves`() {
        val books = listOf(
            book("z", fileType = "epub", duration = null, title = "Zebra"),
            book("a", fileType = "epub", duration = null, title = "Aardvark"),
            book("m", fileType = "m4b", duration = 100.0, title = "Middle"),
        )

        val ordered = LibraryShelves.ordered(books, LibrarySortOrder.DURATION)

        assertEquals(listOf("m", "a", "z"), ordered.map { it.id })
    }

    @Test
    fun `the existing sort orders still behave as they did`() {
        val books = listOf(
            book("b", title = "Beta", addedAt = "2026-01-02"),
            book("a", title = "Alpha", addedAt = "2026-01-03"),
            book("c", title = "Gamma", addedAt = "2026-01-01"),
        )

        assertEquals(
            listOf("a", "b", "c"),
            LibraryShelves.ordered(books, LibrarySortOrder.RECENT).map { it.id },
        )
        assertEquals(
            listOf("a", "b", "c"),
            LibraryShelves.ordered(books, LibrarySortOrder.A_TO_Z).map { it.id },
        )
        assertEquals(
            listOf("c", "b", "a"),
            LibraryShelves.ordered(books, LibrarySortOrder.Z_TO_A).map { it.id },
        )
    }

    // endregion

    // region search

    @Test
    fun `search matches title and author case-insensitively`() {
        val books = listOf(
            book("1", title = "The Silent Patient", author = "Alex Michaelides"),
            book("2", title = "Project Hail Mary", author = "Andy Weir"),
        )

        assertEquals(listOf("1"), LibraryShelves.matching(books, "silent").map { it.id })
        assertEquals(listOf("2"), LibraryShelves.matching(books, "WEIR").map { it.id })
        assertEquals(2, LibraryShelves.matching(books, "  ").size)
    }

    /** Search applies within a shelf, never across it. */
    @Test
    fun `search is scoped to the shelf being viewed`() {
        val books = listOf(
            book("audio", fileType = "m4b", title = "Dragons of Note"),
            book("ebook", fileType = "epub", title = "Dragons in Text"),
        )

        assertEquals(
            listOf("audio"),
            LibraryShelves.visible(
                books, LibraryShelf.AUDIOBOOKS, "dragons", LibrarySortOrder.RECENT,
            ).map { it.id },
        )
        assertEquals(
            listOf("ebook"),
            LibraryShelves.visible(
                books, LibraryShelf.EBOOKS, "dragons", LibrarySortOrder.RECENT,
            ).map { it.id },
        )
    }

    // endregion

    // region the continue card

    /**
     * A shelf must not offer to resume a book that is not on it. Tapping it would open a
     * different surface from the one the tab promises.
     */
    @Test
    fun `the continue card is scoped to its own shelf`() {
        val books = listOf(
            book("audio", fileType = "m4b", position = 500.0),
            book("ebook", fileType = "epub", position = 900.0),
        )

        assertEquals(
            "audio",
            LibraryShelves.featuredOn(books, LibraryShelf.AUDIOBOOKS)?.id,
        )
        assertEquals(
            "ebook",
            LibraryShelves.featuredOn(books, LibraryShelf.EBOOKS)?.id,
        )
    }

    @Test
    fun `a started unfinished book is preferred over the first on the shelf`() {
        val books = listOf(
            book("untouched", fileType = "m4b", position = 0.0),
            book("started", fileType = "m4b", position = 400.0),
        )
        assertEquals("started", LibraryShelves.featuredOn(books, LibraryShelf.AUDIOBOOKS)?.id)
    }

    @Test
    fun `a finished book is not offered as continue`() {
        val books = listOf(
            book("first", fileType = "m4b", position = 0.0),
            book("done", fileType = "m4b", position = 900.0, isFinished = true),
        )
        assertEquals("first", LibraryShelves.featuredOn(books, LibraryShelf.AUDIOBOOKS)?.id)
    }

    @Test
    fun `an empty shelf offers nothing`() {
        assertNull(
            LibraryShelves.featuredOn(listOf(book("a", fileType = "m4b")), LibraryShelf.EBOOKS),
        )
        assertNull(LibraryShelves.featuredOn(emptyList(), LibraryShelf.AUDIOBOOKS))
    }

    // endregion

    // region progress

    /**
     * A progress bar drawn against an unknown total shows a proportion of nothing, which reads
     * as "barely started" however far in the listener actually is. Null means "do not draw one".
     */
    @Test
    fun `progress is absent for a book with no duration`() {
        assertNull(
            LibraryShelves.progressOf(
                book("e", fileType = "epub", duration = null, position = 400.0),
            ),
        )
        assertNull(
            LibraryShelves.progressOf(book("z", fileType = "m4b", duration = 0.0, position = 5.0)),
        )
    }

    @Test
    fun `progress is a clamped fraction of the duration`() {
        assertEquals(
            0.5f,
            LibraryShelves.progressOf(book("a", duration = 1_000.0, position = 500.0))!!,
            0.001f,
        )
        // A position past the end, which a stale sync can produce, must not exceed a full bar.
        assertEquals(
            1f,
            LibraryShelves.progressOf(book("b", duration = 100.0, position = 9_999.0))!!,
            0.001f,
        )
    }

    // endregion

    // region the beta build must not see any of this

    /**
     * The tab row is gated on `NarrationConfig.enabled`, which is the experimental build flag.
     * In a debug or beta build the library screen must render exactly what it renders today.
     *
     * Checked here because the gate is the only thing standing between an in-progress feature and
     * every beta tester's library screen, and it is one dropped condition away from being gone.
     */
    @Test
    fun `narration is off outside the experimental build`() {
        // Unit tests run against the debug variant, where EXPERIMENTAL_BUILD is false.
        assertFalse(
            "narration is enabled in a non-experimental build, so the ebook tab would appear " +
                "for beta testers",
            NarrationConfig.enabled,
        )
    }

    /**
     * And the shelf split itself stays inert: with narration off the screen passes the whole
     * book list through untouched, so an ebook that somehow existed would still be listed rather
     * than silently hidden.
     */
    @Test
    fun `the split hides nothing when it is not used`() {
        val books = listOf(book("a", fileType = "m4b"), book("e", fileType = "epub"))
        val everything = LibraryShelves.booksOn(books, LibraryShelf.AUDIOBOOKS) +
            LibraryShelves.booksOn(books, LibraryShelf.EBOOKS)
        assertEquals(books.size, everything.size)
    }

    // endregion

    // region fixtures

    private fun book(
        id: String,
        fileType: String = "m4b",
        title: String = "Book $id",
        author: String? = "An Author",
        duration: Double? = 3_600.0,
        position: Double = 0.0,
        isFinished: Boolean = false,
        addedAt: String = "2026-01-01",
    ) = LibraryBook(
        id = id,
        fingerprint = BookFingerprint(
            version = 1,
            sha256 = id.padEnd(64, '0'),
            fileSize = 1_024,
            duration = duration,
            fileType = fileType,
        ),
        title = title,
        author = author,
        playbackPositionSeconds = position,
        isFinished = isFinished,
        addedAt = addedAt,
        updatedAt = addedAt,
    )
}
