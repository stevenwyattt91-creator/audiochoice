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
        }
        java.io.File(context.filesDir, "book_covers/${sha256.lowercase()}.image").delete()
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
