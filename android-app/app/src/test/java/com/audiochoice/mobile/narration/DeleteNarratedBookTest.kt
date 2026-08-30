package com.audiochoice.mobile.narration

import com.audiochoice.contracts.BookFingerprint
import com.audiochoice.mobile.data.LibraryBook
import com.audiochoice.mobile.library.LibraryShelves
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

/**
 * Deleting a narrated book has to remove the data that keeps it on the shelf.
 *
 * Holding the library row on the device fixed a book that could not be reached. It also created the
 * opposite failure: the row survives a deletion that only reaches the server, so the next refresh puts
 * the book back and the deletion looks like it did not work.
 */
class DeleteNarratedBookTest {

    @get:Rule
    val folder = TemporaryFolder()

    /** Removing the directory removes the row, so the book stops being listed. */
    @Test
    fun `deleting a narrated book removes its library row`() = runBlocking {
        val store = NarrationStore(folder.root)
        val book = ebook("a".repeat(64))
        store.saveLibraryBook(book.fingerprint.sha256, book)
        store.saveBookText(book.fingerprint.sha256, "The lake lay still.")
        assertEquals(1, store.narratedBooks().size)

        store.deleteBook(book.fingerprint.sha256)

        assertNull("the library row outlived the book", store.libraryBook(book.fingerprint.sha256))
        assertEquals(
            "the deleted book is still listed, so a refresh would put it back on the shelf",
            0,
            store.narratedBooks().size,
        )
        assertNull("the extracted text outlived the book", store.bookText(book.fingerprint.sha256))
    }

    /** Audio goes with it, since it is the largest thing a deletion is expected to reclaim. */
    @Test
    fun `deleting a narrated book reclaims its audio`() = runBlocking {
        val store = NarrationStore(folder.root)
        val sha = "b".repeat(64)
        store.saveLibraryBook(sha, ebook(sha))
        val audio = store.chapterAudioFile(sha, 0)
        audio.parentFile?.mkdirs()
        audio.writeBytes(ByteArray(4_096))
        assertTrue(store.audioBytes(sha) > 0)

        store.deleteBook(sha)

        assertEquals("audio survived the deletion", 0L, store.audioBytes(sha))
    }

    /** Deleting one book leaves the others alone. */
    @Test
    fun `deleting one narrated book leaves the rest`() = runBlocking {
        val store = NarrationStore(folder.root)
        listOf("a", "b", "c").forEach { letter ->
            val sha = letter.repeat(64)
            store.saveLibraryBook(sha, ebook(sha))
        }

        store.deleteBook("b".repeat(64))

        assertEquals(2, store.narratedBooks().size)
        assertTrue(
            "the wrong book was removed",
            store.narratedBooks().none { it.fingerprint.sha256 == "b".repeat(64) },
        )
    }

    /**
     * The library's delete removes the narration directory, and does not let the server decide.
     *
     * A book that never registered carries an identifier the server has never seen, so requiring the
     * remote call to succeed would make exactly those books impossible to delete — the ones that most
     * need deleting, since they are the ones that went wrong.
     *
     * Checked against source because the view model needs a Context, an API and a store.
     */
    @Test
    fun `the library delete removes narration data without requiring the server`() {
        val body = deleteBody()
        assertTrue(
            "the library delete no longer removes the narration directory, so the local row " +
                "survives and the book returns on the next refresh",
            body.contains("NarrationStore(context.filesDir)") && body.contains(".deleteBook("),
        )
        // The remote call must not gate the local removal.
        val remote = body.indexOf("runCatching { api.deleteBook(accessToken, book.id) }")
        assertTrue(
            "the server call is no longer isolated, so a book the server does not know about " +
                "could not be deleted at all",
            remote > 0,
        )
        assertTrue(
            "the narration directory is removed before the server is even told",
            body.indexOf("NarrationStore(context.filesDir)") > remote,
        )
    }

    /** An audiobook's deletion is untouched, which is every book in a shipping build. */
    @Test
    fun `an audiobook deletion is unchanged`() {
        val body = deleteBody()
        assertTrue(
            "the audiobook path no longer requires the server delete to succeed, which changes " +
                "behaviour in beta and release",
            body.contains("api.deleteBook(accessToken, book.id)\n                    localAudio.remove("),
        )
        assertFalse(
            "the narrated branch is not gated on the experimental build, so it would run in a " +
                "shipping build where no narrated book can exist",
            !body.contains("NarrationConfig.enabled"),
        )
    }

    private fun deleteBody(): String {
        val path = "src/main/java/com/audiochoice/mobile/library/LibraryViewModel.kt"
        val file = listOf(File(path), File("app/$path"), File("../app/$path"))
            .firstOrNull(File::isFile)
        assertTrue("could not locate $path", file != null)
        val source = file!!.readText()
        val start = source.indexOf("    fun delete(accessToken: String")
        assertTrue("delete was not found", start >= 0)
        val end = source.indexOf("\n    fun load(", start)
        assertTrue("the end of delete was not found", end > start)
        return source.substring(start, end)
    }

    private fun ebook(sha: String) = LibraryBook(
        id = sha,
        fingerprint = BookFingerprint(
            version = 1,
            sha256 = sha,
            fileSize = 1_024,
            duration = null,
            fileType = LibraryShelves.EBOOK_FILE_TYPE,
        ),
        title = "King Sorrow",
        addedAt = "2026-08-30T00:00:00Z",
        updatedAt = "2026-08-30T00:00:00Z",
    )
}
