package com.audiochoice.mobile.data

import android.content.Context
import android.net.Uri
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.first
import kotlinx.serialization.encodeToString
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.json.Json
import java.security.MessageDigest
import com.audiochoice.contracts.ScanEvent
import com.audiochoice.mobile.reader.ReaderPosition
import com.audiochoice.mobile.reader.ReaderSettings
import kotlinx.serialization.Serializable

private val Context.localAudioDataStore by preferencesDataStore("local_audio_files")

data class LocalAudioMatch(
    val uri: Uri,
    val chapters: List<AudioChapter>,
)

@Serializable
data class OfflineBookPlayback(
    val scannerVersion: String? = null,
    val events: List<ScanEvent> = emptyList(),
    val bookmarks: List<LibraryBookmark> = emptyList(),
    val disabledCategoryIDs: List<String> = emptyList(),
    val disabledGroupIDs: List<String> = emptyList(),
    val disabledEventKeys: List<String> = emptyList(),
    val disabledAggregateKeys: List<String> = emptyList(),
)

@Serializable
data class PendingBookmark(
    val clientID: String,
    val positionSeconds: Double,
    val createdAt: String,
)

class LocalAudioStore(private val context: Context) {
    private companion object {
        // Increment whenever the server-side EPUB/audio matching behavior changes.
        // This replaces old locally cached maps without requiring the listener to
        // detach and reattach their EPUB.
        const val READER_ALIGNMENT_VERSION = "4"

        /**
         * App-private import pipeline directories that may hold reclaimable
         * copies. `AudioChoice/Audiobooks` is deliberately excluded: the listener
         * opts into that copy explicitly, so it is not treated as scratch space.
         */
        val PURGEABLE_AUDIO_DIRECTORIES = listOf(
            "playback_audio",
            "incoming",
            "converted-audiobooks",
        )

        /** Leaves recently touched files alone so an in-flight import is safe. */
        const val STALE_FILE_THRESHOLD_MS = 5 * 60 * 1000L
    }

    private val json = Json { ignoreUnknownKeys = true }
    private fun key(sha256: String) = stringPreferencesKey("audio_${sha256.lowercase()}")
    private fun chapterKey(sha256: String) = stringPreferencesKey("chapters_${sha256.lowercase()}")
    private fun disabledFilterKey(sha256: String) = stringPreferencesKey("disabled_filters_${sha256.lowercase()}")
    private fun epubKey(sha256: String) = stringPreferencesKey("epub_${sha256.lowercase()}")
    private fun epubAlignmentKey(sha256: String) = stringPreferencesKey("epub_alignment_${sha256.lowercase()}")
    private fun epubAlignmentTextKey(sha256: String) = stringPreferencesKey("epub_alignment_text_${sha256.lowercase()}")
    private fun epubAlignmentVersionKey(sha256: String) = stringPreferencesKey("epub_alignment_version_${sha256.lowercase()}")
    private fun libraryKey(accountID: String) = stringPreferencesKey("library_${accountID.lowercase()}")
    private fun offlinePlaybackKey(sha256: String) = stringPreferencesKey("offline_playback_${sha256.lowercase()}")
    private fun pendingBookmarksKey(sha256: String) = stringPreferencesKey("pending_bookmarks_${sha256.lowercase()}")
    private fun filterSettingsDirtyKey(sha256: String) = stringPreferencesKey("filter_settings_dirty_${sha256.lowercase()}")
    private fun readerPositionKey(sha256: String) = stringPreferencesKey("reader_position_${sha256.lowercase()}")

    /** Device-wide rather than per-book, so text size and theme carry across books. */
    private val readerSettingsKey = stringPreferencesKey("reader_settings")

    suspend fun saveReaderSettings(settings: ReaderSettings) {
        context.localAudioDataStore.edit { it[readerSettingsKey] = json.encodeToString(settings) }
    }

    suspend fun readerSettings(): ReaderSettings =
        context.localAudioDataStore.data.first()[readerSettingsKey]
            ?.let { runCatching { json.decodeFromString<ReaderSettings>(it) }.getOrNull() }
            ?: ReaderSettings()

    suspend fun saveReaderPosition(sha256: String, position: ReaderPosition) {
        context.localAudioDataStore.edit { it[readerPositionKey(sha256)] = json.encodeToString(position) }
    }

    suspend fun readerPosition(sha256: String): ReaderPosition =
        context.localAudioDataStore.data.first()[readerPositionKey(sha256)]
            ?.let { runCatching { json.decodeFromString<ReaderPosition>(it) }.getOrNull() }
            ?: ReaderPosition()

    suspend fun saveLibrarySnapshot(accountID: String, books: List<LibraryBook>) {
        context.localAudioDataStore.edit { it[libraryKey(accountID)] = json.encodeToString(books) }
    }

    suspend fun librarySnapshot(accountID: String): List<LibraryBook> =
        context.localAudioDataStore.data.first()[libraryKey(accountID)]
            ?.let { runCatching { json.decodeFromString<List<LibraryBook>>(it) }.getOrNull() }
            .orEmpty()

    suspend fun saveOfflinePlayback(sha256: String, value: OfflineBookPlayback) {
        context.localAudioDataStore.edit { it[offlinePlaybackKey(sha256)] = json.encodeToString(value) }
    }

    suspend fun offlinePlayback(sha256: String): OfflineBookPlayback =
        context.localAudioDataStore.data.first()[offlinePlaybackKey(sha256)]
            ?.let { runCatching { json.decodeFromString<OfflineBookPlayback>(it) }.getOrNull() }
            ?: OfflineBookPlayback()

    suspend fun pendingBookmarks(sha256: String): List<PendingBookmark> =
        context.localAudioDataStore.data.first()[pendingBookmarksKey(sha256)]
            ?.let { runCatching { json.decodeFromString<List<PendingBookmark>>(it) }.getOrNull() }
            .orEmpty()

    suspend fun addPendingBookmark(sha256: String, bookmark: PendingBookmark) {
        context.localAudioDataStore.edit { preferences ->
            val current = preferences[pendingBookmarksKey(sha256)]
                ?.let { runCatching { json.decodeFromString<List<PendingBookmark>>(it) }.getOrNull() }
                .orEmpty()
            preferences[pendingBookmarksKey(sha256)] = json.encodeToString(current + bookmark)
        }
    }

    suspend fun removePendingBookmark(sha256: String, clientID: String) {
        context.localAudioDataStore.edit { preferences ->
            val remaining = preferences[pendingBookmarksKey(sha256)]
                ?.let { runCatching { json.decodeFromString<List<PendingBookmark>>(it) }.getOrNull() }
                .orEmpty()
                .filterNot { it.clientID == clientID }
            preferences[pendingBookmarksKey(sha256)] = json.encodeToString(remaining)
        }
    }

    suspend fun markFilterSettingsDirty(sha256: String, dirty: Boolean) {
        context.localAudioDataStore.edit { it[filterSettingsDirtyKey(sha256)] = dirty.toString() }
    }

    suspend fun filterSettingsDirty(sha256: String): Boolean =
        context.localAudioDataStore.data.first()[filterSettingsDirtyKey(sha256)]?.toBoolean() == true

    suspend fun saveEpub(sha256: String, uri: Uri) {
        context.localAudioDataStore.edit { it[epubKey(sha256)] = uri.toString() }
    }

    suspend fun epub(sha256: String): Uri? =
        context.localAudioDataStore.data.first()[epubKey(sha256)]?.let(Uri::parse)

    private fun epubTextFile(sha256: String): java.io.File =
        java.io.File(java.io.File(context.filesDir, "epub_text"), "${sha256.lowercase()}.txt")

    /**
     * Caches the extracted EPUB text so opening a book does not re-unzip and
     * re-decode the entire file every time.
     *
     * Deliberately a file rather than a DataStore entry: a novel is hundreds of
     * kilobytes to a few megabytes, and Preferences DataStore holds its whole
     * document in memory and rewrites it on every edit.
     */
    suspend fun saveEpubText(sha256: String, text: String) {
        kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
            runCatching {
                val file = epubTextFile(sha256)
                file.parentFile?.mkdirs()
                file.writeText(text)
            }
        }
    }

    suspend fun epubText(sha256: String): String? =
        kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
            epubTextFile(sha256).takeIf(java.io.File::isFile)
                ?.let { runCatching { it.readText() }.getOrNull() }
                ?.takeIf(String::isNotBlank)
        }

    suspend fun saveEpubAlignment(sha256: String, ranges: List<ReaderTimingRange>, epubText: String) {
        context.localAudioDataStore.edit {
            it[epubAlignmentKey(sha256)] = json.encodeToString(ranges)
            it[epubAlignmentTextKey(sha256)] = textHash(epubText)
            it[epubAlignmentVersionKey(sha256)] = READER_ALIGNMENT_VERSION
        }
    }

    suspend fun epubAlignment(sha256: String): List<ReaderTimingRange> =
        context.localAudioDataStore.data.first()[epubAlignmentKey(sha256)]
            ?.let { runCatching { json.decodeFromString<List<ReaderTimingRange>>(it) }.getOrNull() }
            .orEmpty()

    /**
     * Detaches a reading edition without touching the audiobook itself, so the
     * listener can swap in a different EPUB or reclaim the cached text.
     */
    suspend fun removeEpub(sha256: String) {
        context.localAudioDataStore.edit {
            it.remove(epubKey(sha256))
            it.remove(epubAlignmentKey(sha256))
            it.remove(epubAlignmentTextKey(sha256))
            it.remove(epubAlignmentVersionKey(sha256))
            it.remove(readerPositionKey(sha256))
        }
        kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
            epubTextFile(sha256).delete()
        }
    }

    suspend fun epubAlignmentMatches(sha256: String, epubText: String): Boolean =
        context.localAudioDataStore.data.first().let { preferences ->
            preferences[epubAlignmentTextKey(sha256)] == textHash(epubText) &&
                preferences[epubAlignmentVersionKey(sha256)] == READER_ALIGNMENT_VERSION
        }

    suspend fun save(sha256: String, uri: Uri, chapters: List<AudioChapter>, coverBytes: ByteArray? = null) {
        context.localAudioDataStore.edit {
            it[key(sha256)] = uri.toString()
            it[chapterKey(sha256)] = json.encodeToString(chapters)
        }
        coverBytes?.let {
            val directory = java.io.File(context.filesDir, "book_covers").apply { mkdirs() }
            java.io.File(directory, "${sha256.lowercase()}.image").writeBytes(it)
        }
    }

    suspend fun find(sha256: String): Uri? =
        context.localAudioDataStore.data.first()[key(sha256)]?.let(Uri::parse)

    /**
     * Recovers a local file when a server-side catalog match has replaced the
     * file's original fingerprint. This is only used when the direct lookup
     * failed, and requires every meaningful word from the library title to be
     * present in the audio's embedded title before reattaching it.
     */
    suspend fun findLikelyMatch(title: String): LocalAudioMatch? =
        kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
            val wanted = titleTokens(title)
            if (wanted.isEmpty()) return@withContext null
            val preferences = context.localAudioDataStore.data.first()
            preferences.asMap().entries
                .asSequence()
                .mapNotNull { entry ->
                    val name = entry.key.name
                    if (!name.startsWith("audio_")) return@mapNotNull null
                    val hash = name.removePrefix("audio_")
                    val uri = (entry.value as? String)?.let(Uri::parse) ?: return@mapNotNull null
                    val metadataTitle = runCatching {
                        val retriever = android.media.MediaMetadataRetriever()
                        try {
                            retriever.setDataSource(context, uri)
                            retriever.extractMetadata(android.media.MediaMetadataRetriever.METADATA_KEY_TITLE).orEmpty()
                        } finally {
                            retriever.release()
                        }
                    }.getOrDefault("")
                    val candidate = titleTokens(metadataTitle)
                    if (wanted.all(candidate::contains)) {
                        LocalAudioMatch(
                            uri = uri,
                            chapters = preferences[chapterKey(hash)]
                                ?.let { runCatching { json.decodeFromString<List<AudioChapter>>(it) }.getOrNull() }
                                .orEmpty(),
                        )
                    } else null
                }
                .firstOrNull()
        }

    suspend fun chapters(sha256: String): List<AudioChapter> =
        context.localAudioDataStore.data.first()[chapterKey(sha256)]
            ?.let { runCatching { json.decodeFromString<List<AudioChapter>>(it) }.getOrNull() }
            .orEmpty()

    fun coverPath(sha256: String): String? =
        java.io.File(context.filesDir, "book_covers/${sha256.lowercase()}.image")
            .takeIf { it.isFile }?.absolutePath

    fun catalogCoverPath(catalogID: String): String? =
        java.io.File(context.filesDir, "catalog_covers/${catalogID.lowercase()}.image")
            .takeIf { it.isFile }?.absolutePath

    suspend fun saveCatalogCover(catalogID: String, bytes: ByteArray): String =
        kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
            val directory = java.io.File(context.filesDir, "catalog_covers").apply { mkdirs() }
            java.io.File(directory, "${catalogID.lowercase()}.image").apply { writeBytes(bytes) }.absolutePath
        }

    suspend fun saveBookCover(sha256: String, bytes: ByteArray): String =
        kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
            val directory = java.io.File(context.filesDir, "book_covers").apply { mkdirs() }
            java.io.File(directory, "${sha256.lowercase()}.image").apply { writeBytes(bytes) }.absolutePath
        }

    suspend fun coverBytes(sha256: String): ByteArray? =
        kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
            coverPath(sha256)?.let { runCatching { java.io.File(it).readBytes() }.getOrNull() }
        }

    suspend fun catalogCoverBytes(catalogID: String): ByteArray? =
        kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
            catalogCoverPath(catalogID)?.let { runCatching { java.io.File(it).readBytes() }.getOrNull() }
        }

    suspend fun ensureCover(sha256: String, uri: Uri): String? = kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
        coverPath(sha256) ?: runCatching {
            val retriever = android.media.MediaMetadataRetriever()
            try {
                retriever.setDataSource(context, uri)
                retriever.embeddedPicture?.let { bytes ->
                    val directory = java.io.File(context.filesDir, "book_covers").apply { mkdirs() }
                    java.io.File(directory, "${sha256.lowercase()}.image").apply { writeBytes(bytes) }.absolutePath
                }
            } finally {
                retriever.release()
            }
        }.getOrNull()
    }

    suspend fun remove(sha256: String) {
        context.localAudioDataStore.edit {
            it.remove(key(sha256))
            it.remove(chapterKey(sha256))
            it.remove(disabledFilterKey(sha256))
            it.remove(epubKey(sha256))
            it.remove(epubAlignmentKey(sha256))
            it.remove(epubAlignmentTextKey(sha256))
            it.remove(epubAlignmentVersionKey(sha256))
            // These three were previously left behind, so a deleted book kept its
            // cached scan, disabled filters and unsent bookmarks on the device --
            // and re-importing the same edition silently re-adopted them.
            it.remove(offlinePlaybackKey(sha256))
            it.remove(pendingBookmarksKey(sha256))
            it.remove(filterSettingsDirtyKey(sha256))
            it.remove(readerPositionKey(sha256))
        }
        java.io.File(context.filesDir, "book_covers/${sha256.lowercase()}.image").delete()
        epubTextFile(sha256).delete()
        // The audio file itself is reclaimed by reference count rather than by
        // name: one file is registered under both the source fingerprint and the
        // canonical edition fingerprint, so deleting it directly here would break
        // the surviving entry.
        purgeOrphanedAudioFiles()
    }

    /**
     * Deletes app-private audio files that no library entry points at any more,
     * and returns the number of bytes reclaimed.
     *
     * Deleting a book used to leave its audio behind forever, and a single import
     * could leave up to four copies of the same audiobook on the device
     * (`incoming/` for companion transfers, `converted-audiobooks/` for AAX
     * remuxes, `playback_audio/` for the stable copy, plus any `.partial`
     * remnant). Ten deleted 2 GB books meant 20 GB unreclaimable from inside the
     * app.
     *
     * This is deliberately reference-based rather than directory-based:
     * `preserveForPlayback` returns its source unchanged when the file already
     * lives in app-private storage, so the file in `incoming/` or
     * `converted-audiobooks/` is frequently the live playback file. Only
     * `file://` URIs under `filesDir` are ever considered; a `content://` URI
     * points at the listener's own file and is never touched.
     */
    suspend fun purgeOrphanedAudioFiles(): Long =
        kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
            val referenced = context.localAudioDataStore.data.first()
                .asMap()
                .filterKeys { it.name.startsWith("audio_") }
                .values
                .filterIsInstance<String>()
                .mapNotNull { value ->
                    runCatching { Uri.parse(value) }.getOrNull()
                        ?.takeIf { it.scheme.equals("file", ignoreCase = true) }
                        ?.path
                        ?.let { path -> runCatching { java.io.File(path).canonicalPath }.getOrNull() }
                }
                .toSet()

            var reclaimed = 0L
            val now = System.currentTimeMillis()
            PURGEABLE_AUDIO_DIRECTORIES.forEach { name ->
                val directory = java.io.File(context.filesDir, name)
                if (!directory.isDirectory) return@forEach
                directory.listFiles()?.forEach forEachFile@{ file ->
                    if (!file.isFile) return@forEachFile
                    // Never race an import that is still writing its file: an
                    // active download or copy keeps a recent modification time.
                    if (now - file.lastModified() < STALE_FILE_THRESHOLD_MS) return@forEachFile
                    val path = runCatching { file.canonicalPath }.getOrNull() ?: return@forEachFile
                    if (path in referenced) return@forEachFile
                    val size = file.length()
                    if (file.delete()) reclaimed += size
                }
            }
            reclaimed
        }

    suspend fun saveDisabledFilters(sha256: String, groupIDs: Set<String>) {
        context.localAudioDataStore.edit {
            it[disabledFilterKey(sha256)] = json.encodeToString(groupIDs.map(String::lowercase).sorted())
        }
    }

    suspend fun disabledFilters(sha256: String): Set<String> =
        context.localAudioDataStore.data.first()[disabledFilterKey(sha256)]
            ?.let { runCatching { json.decodeFromString<List<String>>(it).toSet() }.getOrNull() }
            .orEmpty()

    private fun textHash(text: String): String = MessageDigest.getInstance("SHA-256")
        .digest(text.toByteArray()).joinToString("") { "%02x".format(it) }

    private fun titleTokens(value: String): Set<String> = value.lowercase()
        .replace(Regex("\\b(dramatized|adaptation|audiobook|the)\\b"), " ")
        .split(Regex("[^a-z0-9]+"))
        .filter { it.length > 1 }
        .toSet()
}
