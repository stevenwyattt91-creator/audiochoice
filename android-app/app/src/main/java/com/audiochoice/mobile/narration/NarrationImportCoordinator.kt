package com.audiochoice.mobile.narration

import android.content.ContentResolver
import android.content.Intent
import android.net.Uri
import android.provider.OpenableColumns
import com.audiochoice.mobile.data.AudioChoiceApi
import com.audiochoice.mobile.data.LocalAudioStore
import com.audiochoice.mobile.reader.EpubTextReader
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File

/**
 * Decides whether a picked file is an EPUB, and wires [NarrationImporter] to the app's stores.
 *
 * Exists so `ImportViewModel` gains a branch rather than a dependency graph: the importer needs
 * seven collaborators to stay testable, and assembling them in the view model would put Android
 * plumbing in the middle of the audiobook import path.
 */
class NarrationImportCoordinator(
    private val api: AudioChoiceApi,
    private val localAudio: LocalAudioStore,
    filesDirectory: File,
) {
    private val store = NarrationStore(filesDirectory)

    suspend fun import(
        uri: Uri,
        resolver: ContentResolver,
        accessToken: String,
    ): NarrationImportOutcome {
        val displayName = displayNameOf(uri, resolver)
        val declaredSize = declaredSizeOf(uri, resolver)

        val importer = NarrationImporter(
            store = store,
            takePersistablePermission = {
                // The grant is what makes the file readable after a restart. Without it the
                // import would succeed now and the book would break silently later, so a
                // failure here abandons the import rather than continuing hopefully.
                runCatching {
                    resolver.takePersistableUriPermission(
                        uri, Intent.FLAG_GRANT_READ_URI_PERMISSION,
                    )
                }.isSuccess
            },
            readDocument = { input ->
                withContext(Dispatchers.Default) {
                    EpubTextReader.readNarrationDocument(input)
                }
            },
            isAlreadyInLibrary = { sha256 ->
                // The device's own library row, which is now the authority for a narrated book.
                //
                // Two earlier readings of this were wrong in opposite directions. Stored Book_Text
                // made a book unrecoverable: the import reported it present and never registered it,
                // so re-importing could not repair a missing library row. `LocalAudioStore.find` was
                // no better -- it reads `audio_<hash>`, a key a narrated ebook never writes, so it is
                // always null for one and answers a question about audiobooks.
                //
                // This row is written by the import itself and does not depend on the network, so it
                // is true exactly when the book is on the shelf.
                store.libraryBook(sha256) != null
            },
            persistSourceLocation = { sha256 -> localAudio.saveEpub(sha256, uri) },
            saveLibraryBook = { request -> api.saveBook(accessToken, request) },
            saveLocalLibraryBook = { book -> store.saveLibraryBook(book.fingerprint.sha256, book) },
            saveCover = { sha256, bytes -> localAudio.saveBookCover(sha256, bytes) },
        )

        return importer.import(
            NarrationImportSource(
                displayName = displayName,
                declaredSize = declaredSize,
                // A factory, because the import reads the file twice: once to fingerprint it,
                // once to extract its text.
                openStream = { runCatching { resolver.openInputStream(uri) }.getOrNull() },
            ),
        )
    }

    private fun displayNameOf(uri: Uri, resolver: ContentResolver): String? = runCatching {
        resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { cursor ->
            if (cursor.moveToFirst() && !cursor.isNull(0)) cursor.getString(0) else null
        }
    }.getOrNull() ?: uri.lastPathSegment

    private fun declaredSizeOf(uri: Uri, resolver: ContentResolver): Long = runCatching {
        resolver.query(uri, arrayOf(OpenableColumns.SIZE), null, null, null)?.use { cursor ->
            if (cursor.moveToFirst() && !cursor.isNull(0)) cursor.getLong(0) else -1L
        } ?: -1L
    }.getOrNull() ?: -1L

    companion object {
        /**
         * MIME types offered by the file picker.
         *
         * Mirrors the array the read-along attach path already uses. The octet-stream and
         * wildcard entries are there because a great many providers report an EPUB as one or the
         * other, and a strict epub+zip filter makes the listener's own file unselectable.
         */
        val PICKER_MIME_TYPES = arrayOf(
            "audio/*",
            "application/epub+zip",
            "application/octet-stream",
            "*/*",
        )

        /** The picker offered when narration is unavailable. Unchanged from what ships today. */
        val AUDIO_ONLY_PICKER_MIME_TYPES = arrayOf("audio/*", "application/octet-stream")

        /**
         * Whether a picked file should be imported as an ebook.
         *
         * Decided on the file name, not the reported MIME type, because the permissive types
         * above mean the reported type is frequently `application/octet-stream` for both an EPUB
         * and an M4B. The name is what the listener chose and what the provider is most reliable
         * about.
         */
        fun isEpub(fileName: String?): Boolean =
            fileName?.substringAfterLast('.', "")?.equals("epub", ignoreCase = true) == true
    }
}
