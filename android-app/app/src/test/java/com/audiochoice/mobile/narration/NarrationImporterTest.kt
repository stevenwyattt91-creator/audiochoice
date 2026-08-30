package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.LibraryBookUpsertRequest
import com.audiochoice.mobile.reader.EpubDocument
import com.audiochoice.mobile.reader.NavigationOutline
import com.audiochoice.mobile.reader.NavigationSource
import com.audiochoice.mobile.reader.ResourceSpan
import com.audiochoice.mobile.data.SourceRange
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.security.MessageDigest
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

class NarrationImporterTest {

    @get:Rule
    val temporaryFolder = TemporaryFolder()

    // region fingerprint

    /**
     * The fingerprint is what the duplicate check keys on, and the duplicate check is what
     * protects hours of already-rendered audio. Getting `fileType` or `duration` wrong here
     * would put a bogus runtime on every library and progress surface.
     */
    @Test
    fun `an imported EPUB is fingerprinted as epub with no duration`(): Unit = runBlocking {
        val bytes = "a book's worth of bytes".toByteArray()
        val requests = mutableListOf<LibraryBookUpsertRequest>()
        val outcome = importer(requests = requests).import(source(bytes, "Novel.epub"))

        val imported = outcome as NarrationImportOutcome.Imported
        assertEquals(1, imported.fingerprint.version)
        assertEquals("epub", imported.fingerprint.fileType)
        assertNull("a narrated book has no runtime until it is rendered",
            imported.fingerprint.duration)
        assertEquals(bytes.size.toLong(), imported.fingerprint.fileSize)
        assertEquals(sha256Of(bytes), imported.fingerprint.sha256)

        // And the same reaches the library row.
        assertEquals(1, requests.size)
        assertEquals("epub", requests.single().fingerprint.fileType)
        assertNull(requests.single().fingerprint.duration)
    }

    /**
     * The digest is lowercase because the on-device narration layout is a directory named
     * after it. Comparisons elsewhere are case-insensitive, but a filesystem path is not.
     */
    @Test
    fun `the digest is lowercase hexadecimal`(): Unit = runBlocking {
        val outcome = importer().import(source("text".toByteArray(), "Book.epub"))
        val sha = (outcome as NarrationImportOutcome.Imported).fingerprint.sha256
        assertEquals(64, sha.length)
        assertEquals(sha.lowercase(), sha)
    }

    /**
     * The byte count is measured, not taken from the provider, which reports -1 for many
     * sources. Since the count is part of the fingerprint, trusting a wrong one would give
     * one book two identities and defeat the duplicate check.
     */
    @Test
    fun `the byte count is measured rather than believed`(): Unit = runBlocking {
        val bytes = ByteArray(4_096) { it.toByte() }
        val outcome = importer().import(
            NarrationImportSource(
                displayName = "Book.epub",
                declaredSize = -1L,
                openStream = { ByteArrayInputStream(bytes) },
            ),
        )
        assertEquals(
            4_096L,
            (outcome as NarrationImportOutcome.Imported).fingerprint.fileSize,
        )
    }

    /**
     * The requirement is 50 MB fingerprinted within 30 seconds. Measured on the streaming
     * path the import actually uses, so a change to buffering that made it quadratic would
     * be caught here rather than by a listener watching a spinner.
     */
    @Test
    fun `fifty megabytes are fingerprinted well inside thirty seconds`(): Unit = runBlocking {
        val bytes = ByteArray(50 * 1024 * 1024) { (it % 251).toByte() }
        val started = System.nanoTime()
        val outcome = importer().import(
            NarrationImportSource(
                displayName = "Long.epub",
                declaredSize = bytes.size.toLong(),
                openStream = { ByteArrayInputStream(bytes) },
            ),
        )
        val elapsedMs = (System.nanoTime() - started) / 1_000_000

        assertTrue(outcome is NarrationImportOutcome.Imported)
        assertEquals(
            (50 * 1024 * 1024).toLong(),
            (outcome as NarrationImportOutcome.Imported).fingerprint.fileSize,
        )
        assertTrue("fingerprinting 50 MB took ${elapsedMs}ms", elapsedMs < 30_000)
    }

    /** An empty file has nothing to fingerprint and nothing to read aloud. */
    @Test
    fun `an empty file is not imported`(): Unit = runBlocking {
        val outcome = importer().import(source(ByteArray(0), "Empty.epub"))
        assertTrue(outcome is NarrationImportOutcome.Failed)
    }

    @Test
    fun `an unopenable file is reported rather than crashing`(): Unit = runBlocking {
        val outcome = importer().import(
            NarrationImportSource("Book.epub", 10L, openStream = { null }),
        )
        assertTrue(outcome is NarrationImportOutcome.Failed)
    }

    // endregion

    // region permission

    /**
     * Without the persistable permission the file reads now and fails after a restart, so
     * importing would create a library entry that breaks silently later. Nothing may be
     * written, and the file must not even be read.
     */
    @Test
    fun `a permission failure creates no book and reads nothing`(): Unit = runBlocking {
        var streamsOpened = 0
        val requests = mutableListOf<LibraryBookUpsertRequest>()
        val store = store()
        val importer = NarrationImporter(
            store = store,
            takePersistablePermission = { false },
            readDocument = { document() },
            isAlreadyInLibrary = { false },
            persistSourceLocation = {},
            saveLibraryBook = { requests += it; savedRow(it) },
            saveLocalLibraryBook = {},
            saveCover = { _, _ -> },
        )

        val outcome = importer.import(
            NarrationImportSource("Book.epub", 4L, openStream = {
                streamsOpened += 1
                ByteArrayInputStream("text".toByteArray())
            }),
        )

        assertEquals(NarrationImportOutcome.PermissionRefused, outcome)
        assertEquals("the file was read before the permission was secured", 0, streamsOpened)
        assertTrue("a library row was created without a durable permission", requests.isEmpty())
        assertNull(store.textScan(sha256Of("text".toByteArray())))
    }

    // endregion

    // region duplicates

    /**
     * Re-importing a book after moving it in a file manager is the ordinary way here. The
     * plan, the rendered audio and the position all survive; only the recorded location is
     * refreshed. Losing hours of synthesised audio for a moved file would be indefensible.
     */
    @Test
    fun `a duplicate refreshes the location and leaves every artifact intact`(): Unit =
        runBlocking {
            val bytes = "the same book".toByteArray()
            val sha = sha256Of(bytes)
            val store = store()
            store.saveBookText(sha, "already extracted text")
            store.saveTextScan(
                sha,
                StoredTextScan(emptyList(), "v1", "2.0", null, "already extracted text".length),
            )

            var locationsPersisted = 0
            var documentsRead = 0
            val requests = mutableListOf<LibraryBookUpsertRequest>()
            val importer = NarrationImporter(
                store = store,
                takePersistablePermission = { true },
                readDocument = { documentsRead += 1; document() },
                isAlreadyInLibrary = { it == sha },
                persistSourceLocation = { locationsPersisted += 1 },
                saveLibraryBook = { requests += it; savedRow(it) },
                saveLocalLibraryBook = {},
                saveCover = { _, _ -> },
            )

            val outcome = importer.import(source(bytes, "Moved.epub"))

            assertEquals(NarrationImportOutcome.AlreadyInLibrary(sha), outcome)
            assertEquals("the location was not refreshed", 1, locationsPersisted)
            assertTrue("a second library row was created", requests.isEmpty())
            assertEquals("the archive was decoded again for a book already imported",
                0, documentsRead)
            // The artifacts that cost real time and money are untouched.
            assertEquals("already extracted text", store.bookText(sha))
            assertNotNull("a duplicate import discarded the stored scan", store.textScan(sha))
        }

    // endregion

    // region declined books

    /**
     * A declined book keeps no text on the device. The validator returns no document for a
     * decline, so the purge is enforced by the type; this checks the importer honours it and
     * clears anything a previous attempt left behind.
     */
    @Test
    fun `a declined book leaves no text on the device`(): Unit = runBlocking {
        val bytes = "short".toByteArray()
        val sha = sha256Of(bytes)
        val store = store()
        store.saveBookText(sha, "text from an earlier attempt")

        val requests = mutableListOf<LibraryBookUpsertRequest>()
        val importer = NarrationImporter(
            store = store,
            takePersistablePermission = { true },
            // Too little text to be worth narrating.
            readDocument = { document(text = "a b c") },
            isAlreadyInLibrary = { false },
            persistSourceLocation = {},
            saveLibraryBook = { requests += it; savedRow(it) },
            saveLocalLibraryBook = {},
            saveCover = { _, _ -> },
        )

        val outcome = importer.import(source(bytes, "Tiny.epub"))

        val declined = outcome as NarrationImportOutcome.Declined
        assertTrue(declined.reason is DeclineReason.TooLittleText)
        assertTrue("the decline carried no listener-facing copy",
            declined.message.headline.isNotBlank())
        assertTrue("a declined book was added to the library", requests.isEmpty())
        assertNull("a declined book left its text on the device", store.bookText(sha))
    }

    // endregion

    // region metadata

    @Test
    fun `the book's own title and author are used when present`() {
        val derived = NarrationImporter.titleFor(
            document(title = "A Real Title", author = "A Real Author"),
            displayName = "whatever.epub",
            sha256 = "abcdef1234567890",
        )
        assertEquals("A Real Title", derived.title)
        assertEquals("A Real Author", derived.author)
        assertFalse(derived.wasDerived)
    }

    @Test
    fun `a missing title falls back to the filename without its extension`() {
        val derived = NarrationImporter.titleFor(
            document(title = null),
            displayName = "The Silent Patient.epub",
            sha256 = "abcdef1234567890",
        )
        assertEquals("The Silent Patient", derived.title)
        assertTrue("a filename-derived title must be flagged", derived.wasDerived)
    }

    /**
     * The last resort exists so an import never fails for want of a title. A book the
     * listener can see and rename beats a refused import.
     */
    @Test
    fun `a missing title and filename fall back to the hash prefix`() {
        val derived = NarrationImporter.titleFor(
            document(title = null),
            displayName = null,
            sha256 = "ABCDEF1234567890",
        )
        assertEquals("Imported book abcdef12", derived.title)
        assertTrue(derived.wasDerived)
    }

    @Test
    fun `an absent author does not prevent an import`(): Unit = runBlocking {
        val outcome = importer(document = document(author = null))
            .import(source("text".toByteArray(), "Book.epub"))
        val imported = outcome as NarrationImportOutcome.Imported
        assertNull(imported.author)
    }

    @Test
    fun `title and author are truncated to five hundred characters`() {
        val derived = NarrationImporter.titleFor(
            document(title = "T".repeat(900), author = "A".repeat(900)),
            displayName = null,
            sha256 = "abcdef1234567890",
        )
        assertEquals(NarrationImporter.MAXIMUM_FIELD_CHARACTERS, derived.title.length)
        assertEquals(NarrationImporter.MAXIMUM_FIELD_CHARACTERS, derived.author?.length)
    }

    /** A blank title is not a title, and must fall through to the next candidate. */
    @Test
    fun `a blank title falls through to the filename`() {
        val derived = NarrationImporter.titleFor(
            document(title = "   "),
            displayName = "Fallback.epub",
            sha256 = "abcdef1234567890",
        )
        assertEquals("Fallback", derived.title)
        assertTrue(derived.wasDerived)
    }

    // endregion

    // region cover

    @Test
    fun `the manifest cover is stored`(): Unit = runBlocking {
        val coverBytes = byteArrayOf(0x89.toByte(), 0x50, 0x4E, 0x47, 1, 2, 3, 4)
        val archive = zipOf("OEBPS/cover.jpg" to coverBytes, "OEBPS/text.html" to "hi".toByteArray())
        val stored = mutableMapOf<String, ByteArray>()

        val outcome = importer(
            document = document(coverImageEntry = "OEBPS/cover.jpg"),
            onCover = { sha, bytes -> stored[sha] = bytes },
        ).import(source(archive, "Book.epub"))

        assertTrue((outcome as NarrationImportOutcome.Imported).coverStored)
        assertEquals(1, stored.size)
        assertTrue(coverBytes.contentEquals(stored.values.single()))
    }

    /** No cover is a complete outcome, not a failure: a book with no artwork still reads. */
    @Test
    fun `an absent cover leaves the default and completes the import`(): Unit = runBlocking {
        val stored = mutableMapOf<String, ByteArray>()
        val outcome = importer(
            document = document(coverImageEntry = null),
            onCover = { sha, bytes -> stored[sha] = bytes },
        ).import(source("text".toByteArray(), "Book.epub"))

        assertTrue(outcome is NarrationImportOutcome.Imported)
        assertFalse((outcome as NarrationImportOutcome.Imported).coverStored)
        assertTrue(stored.isEmpty())
    }

    @Test
    fun `a cover entry that is not in the archive is ignored`(): Unit = runBlocking {
        val archive = zipOf("OEBPS/text.html" to "hi".toByteArray())
        val outcome = importer(
            document = document(coverImageEntry = "OEBPS/missing.jpg"),
        ).import(source(archive, "Book.epub"))

        assertTrue(outcome is NarrationImportOutcome.Imported)
        assertFalse((outcome as NarrationImportOutcome.Imported).coverStored)
    }

    @Test
    fun `a cover entry is matched case-insensitively and without a leading slash`() {
        val archive = zipOf("OEBPS/Cover.JPG" to byteArrayOf(1, 2, 3))
        val bytes = EpubCoverReader.readEntry(ByteArrayInputStream(archive), "/oebps/cover.jpg")
        assertNotNull(bytes)
        assertTrue(byteArrayOf(1, 2, 3).contentEquals(bytes!!))
    }

    /** Half an image decodes to nothing, so an oversized entry is refused outright. */
    @Test
    fun `an implausibly large cover entry is refused rather than truncated`() {
        val huge = ByteArray(EpubCoverReader.MAXIMUM_COVER_BYTES + 1_024)
        val archive = zipOf("cover.jpg" to huge)
        assertNull(EpubCoverReader.readEntry(ByteArrayInputStream(archive), "cover.jpg"))
    }

    @Test
    fun `a corrupt archive yields no cover rather than throwing`() {
        assertNull(
            EpubCoverReader.readEntry(
                ByteArrayInputStream("not a zip at all".toByteArray()),
                "cover.jpg",
            ),
        )
    }

    /**
     * The saved library row has to come back out of the import.
     *
     * This was a real bug: the importer discarded it, so the screen had nothing to publish, so the
     * library list never reloaded, so the Ebooks tab -- which only appears once an ebook exists --
     * never showed up. The book was on the server and on the device, and invisible.
     */
    @Test
    fun `the saved library row is carried out of the import`(): Unit = runBlocking {
        val bytes = "text".toByteArray()
        val outcome = importer().import(source(bytes, "Novel.epub"))

        val imported = outcome as NarrationImportOutcome.Imported
        assertEquals(
            "the row the server saved was not carried back",
            "server-row-${sha256Of(bytes).take(8)}",
            imported.libraryBook.id,
        )
        // And it must describe the same book, or the library would list something else.
        assertEquals(imported.fingerprint.sha256, imported.libraryBook.fingerprint.sha256)
        assertEquals("epub", imported.libraryBook.fingerprint.fileType)
        assertEquals(imported.title, imported.libraryBook.title)
    }

    // endregion

    // region library failure

    /**
     * An unreachable server no longer costs the listener the book.
     *
     * Everything a narrated book needs is on the device, so the import records the library row
     * itself and carries on. Refusing the import would deny someone a book they already have, and
     * recording the row only on the server is what let one vanish from the shelf with no way to
     * bring it back.
     */
    @Test
    fun `an unreachable library still imports and records the row locally`(): Unit = runBlocking {
        val bytes = "text".toByteArray()
        val store = store()
        val localRows = mutableListOf<com.audiochoice.mobile.data.LibraryBook>()
        val importer = NarrationImporter(
            store = store,
            takePersistablePermission = { true },
            readDocument = { document() },
            isAlreadyInLibrary = { false },
            persistSourceLocation = {},
            saveLibraryBook = { throw java.io.IOException("offline") },
            saveLocalLibraryBook = { localRows += it },
            saveCover = { _, _ -> },
        )
        val outcome = importer.import(source(bytes, "Book.epub"))
        assertTrue(
            "an offline import failed instead of recording the book on the device",
            outcome is NarrationImportOutcome.Imported,
        )
        assertEquals("no local library row was recorded", 1, localRows.size)
        // The shelf is chosen by this, so an offline import must still land among the ebooks.
        assertEquals("epub", localRows.single().fingerprint.fileType)
        assertNotNull(
            "Book_Text was not stored for a book that imported successfully",
            store.bookText(sha256Of(bytes)),
        )
    }

    // endregion

    // region generators and fixtures

    private fun store() = NarrationStore(temporaryFolder.root)

    /** What the server returns for an upsert, which the importer must carry back out. */
    private fun savedRow(request: com.audiochoice.mobile.data.LibraryBookUpsertRequest) =
        com.audiochoice.mobile.data.LibraryBook(
            id = "server-row-${request.fingerprint.sha256.take(8)}",
            fingerprint = request.fingerprint,
            title = request.title,
            author = request.author,
            addedAt = "2026-01-01",
            updatedAt = "2026-01-01",
        )

    private fun importer(
        document: EpubDocument = document(),
        requests: MutableList<LibraryBookUpsertRequest> = mutableListOf(),
        onCover: suspend (String, ByteArray) -> Unit = { _, _ -> },
    ) = NarrationImporter(
        store = store(),
        takePersistablePermission = { true },
        readDocument = { document },
        isAlreadyInLibrary = { false },
        persistSourceLocation = {},
        saveLibraryBook = { requests += it; savedRow(it) },
            saveLocalLibraryBook = {},
        saveCover = onCover,
    )

    private fun source(bytes: ByteArray, name: String) = NarrationImportSource(
        displayName = name,
        declaredSize = bytes.size.toLong(),
        openStream = { ByteArrayInputStream(bytes) },
    )

    private fun sha256Of(bytes: ByteArray): String =
        MessageDigest.getInstance("SHA-256").digest(bytes)
            .joinToString("") { "%02x".format(it) }

    private fun zipOf(vararg entries: Pair<String, ByteArray>): ByteArray {
        val output = ByteArrayOutputStream()
        ZipOutputStream(output).use { zip ->
            entries.forEach { (name, bytes) ->
                zip.putNextEntry(ZipEntry(name))
                zip.write(bytes)
                zip.closeEntry()
            }
        }
        return output.toByteArray()
    }

    /**
     * A document that passes validation, so each test can vary only the field it is about.
     */
    private fun document(
        text: String = "Chapter One\n\n" + "The quick brown fox jumped over the lazy dog. "
            .repeat(40),
        title: String? = "A Title",
        author: String? = "An Author",
        coverImageEntry: String? = null,
    ) = EpubDocument(
        text = text,
        extractionVersion = 1,
        language = "en",
        title = title,
        author = author,
        coverImageEntry = coverImageEntry,
        resources = listOf(ResourceSpan("OEBPS/text.html", SourceRange(0, text.length))),
        nonProseRanges = emptyList(),
        anchorOffsets = emptyMap(),
        navigation = NavigationOutline(NavigationSource.EPUB3_NAV, emptyList()),
        declaresNavigation = false,
        encryptedEntries = emptySet(),
        storeDrmResources = emptyList(),
        unreadableSpineEntries = emptyList(),
        declaredSpineEntries = listOf("OEBPS/text.html"),
    )
}
