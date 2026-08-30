package com.audiochoice.mobile.narration

import com.audiochoice.contracts.BookFingerprint
import com.audiochoice.mobile.data.LibraryBook
import com.audiochoice.mobile.library.LibraryShelf
import com.audiochoice.mobile.library.LibraryShelves
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

/**
 * A narrated book's place in the library is held on the device.
 *
 * Everything else about one already is: the file, its extracted text, its rendered audio. Its library
 * row was the single exception, and that is how a book sitting on the phone became unreachable through
 * it — and because the ebook shelf only appears once an ebook is on it, the shelf disappeared too, with
 * re-importing unable to bring either back.
 */
class LocalEbookShelfTest {

    @get:Rule
    val folder = TemporaryFolder()

    @Test
    fun `a recorded row is readable back and lands on the ebook shelf`() = runBlocking {
        val store = NarrationStore(folder.root)
        val book = ebook("a".repeat(64), "King Sorrow")
        store.saveLibraryBook(book.fingerprint.sha256, book)

        val read = store.libraryBook(book.fingerprint.sha256)
        assertNotNull("the recorded library row could not be read back", read)
        assertEquals("King Sorrow", read!!.title)
        // The shelf is chosen from this field, so it has to survive the round trip intact.
        assertEquals(LibraryShelf.EBOOKS, LibraryShelves.shelfFor(read))
    }

    /** Listed by walking the narration directory, so presence of data is what counts. */
    @Test
    fun `every recorded narrated book is listed`() = runBlocking {
        val store = NarrationStore(folder.root)
        listOf("a", "b", "c").forEachIndexed { index, letter ->
            val book = ebook(letter.repeat(64), "Book $index")
            store.saveLibraryBook(book.fingerprint.sha256, book)
        }
        assertEquals(3, store.narratedBooks().size)
        assertTrue(
            "a listed book is not on the ebook shelf, so the tab would stay hidden",
            store.narratedBooks().all { LibraryShelves.shelfFor(it) == LibraryShelf.EBOOKS },
        )
    }

    /**
     * A book directory without a row is skipped rather than breaking the listing.
     *
     * Every book imported before this record existed is in exactly that state, including the one whose
     * disappearance prompted it.
     */
    @Test
    fun `a book directory with no row is skipped`() = runBlocking {
        val store = NarrationStore(folder.root)
        val recorded = ebook("a".repeat(64), "Recorded")
        store.saveLibraryBook(recorded.fingerprint.sha256, recorded)
        // A pre-existing book: narration data present, no library row.
        store.saveBookText("b".repeat(64), "Some extracted text.")

        val listed = store.narratedBooks()
        assertEquals("a directory without a row was counted or threw", 1, listed.size)
        assertEquals("Recorded", listed.single().title)
    }

    /** Unreadable JSON is skipped, not fatal: one damaged file must not hide every other book. */
    @Test
    fun `an unreadable row does not hide the rest`() = runBlocking {
        val store = NarrationStore(folder.root)
        val good = ebook("a".repeat(64), "Good")
        store.saveLibraryBook(good.fingerprint.sha256, good)
        val corrupt = File(store.bookDirectory("b".repeat(64)), NarrationStore.LIBRARY_BOOK_FILE)
        corrupt.parentFile?.mkdirs()
        corrupt.writeText("{ not json")

        assertEquals(1, store.narratedBooks().size)
    }

    /**
     * The merge prefers the server's copy of a book both know about.
     *
     * The server's row carries the identifier and reading position that sync between devices; the local
     * one is a placeholder for as long as the server has not answered. Exercised against the same
     * de-duplication rule the library applies.
     */
    @Test
    fun `a server copy takes precedence over the local placeholder`() {
        val sha = "a".repeat(64)
        val fromServer = ebook(sha, "King Sorrow").copy(
            id = "server-id",
            playbackPositionSeconds = 812.0,
        )
        val local = ebook(sha, "King Sorrow")

        val known = listOf(fromServer).map { it.fingerprint.sha256.lowercase() }.toSet()
        val merged = listOf(fromServer) +
            listOf(local).filter { it.fingerprint.sha256.lowercase() !in known }

        assertEquals("the same book was listed twice", 1, merged.size)
        assertEquals("the local placeholder displaced the server's row", "server-id", merged[0].id)
        assertEquals(
            "the reading position synced from the server was lost",
            812.0,
            merged[0].playbackPositionSeconds,
            0.0,
        )
    }

    /** A book only the device knows about is added rather than dropped. */
    @Test
    fun `a book the server does not know about is still shown`() {
        val audiobook = ebook("f".repeat(64), "An Audiobook")
            .copy(fingerprint = ebook("f".repeat(64), "x").fingerprint.copy(fileType = "m4b"))
        val local = ebook("a".repeat(64), "King Sorrow")

        val known = listOf(audiobook).map { it.fingerprint.sha256.lowercase() }.toSet()
        val merged = listOf(audiobook) +
            listOf(local).filter { it.fingerprint.sha256.lowercase() !in known }

        assertEquals(2, merged.size)
        assertTrue(
            "the locally-held ebook is missing, so the shelf would not appear",
            LibraryShelves.hasEbooks(merged),
        )
    }

    private fun ebook(sha: String, title: String) = LibraryBook(
        id = sha,
        fingerprint = BookFingerprint(
            version = 1,
            sha256 = sha,
            fileSize = 1_024,
            duration = null,
            fileType = LibraryShelves.EBOOK_FILE_TYPE,
        ),
        title = title,
        author = "Joe Hill",
        addedAt = "2026-08-30T00:00:00Z",
        updatedAt = "2026-08-30T00:00:00Z",
    )
}
