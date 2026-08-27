package com.audiochoice.mobile.importing

import android.content.Context
import android.media.MediaMetadataRetriever
import android.net.Uri
import android.provider.OpenableColumns
import com.audiochoice.contracts.BookFingerprint
import com.audiochoice.mobile.data.AudioChapter
import java.security.MessageDigest
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

data class InspectedAudio(
    val fingerprint: BookFingerprint,
    val fileName: String,
    val title: String,
    val contentType: String,
    val chapters: List<AudioChapter>,
    val coverBytes: ByteArray?,
    /** Edition metadata read out of the container, for identification and display. */
    val tags: AudioEditionTags = AudioEditionTags(),
    /**
     * False when the title had to be guessed from the filename, which means the
     * edition is not actually known. Callers should present such a book as needing
     * identification rather than as fact.
     */
    val isTitleFromMetadata: Boolean = false,
)

class AudioFileInspector(private val context: Context) {
    suspend fun inspect(uri: Uri): InspectedAudio = withContext(Dispatchers.IO) {
        val resolver = context.contentResolver
        var name = "Imported audiobook"
        var size = -1L
        resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { cursor ->
            if (cursor.moveToFirst()) {
                name = cursor.getString(0) ?: name
                size = if (cursor.isNull(1)) -1L else cursor.getLong(1)
            }
        }
        val digest = MessageDigest.getInstance("SHA-256")
        var measuredSize = 0L
        resolver.openInputStream(uri).use { input ->
            requireNotNull(input) { "The selected audiobook could not be opened." }
            val buffer = ByteArray(1024 * 1024)
            while (true) {
                val count = input.read(buffer)
                if (count < 0) break
                digest.update(buffer, 0, count)
                measuredSize += count
            }
        }
        if (size < 0) size = measuredSize
        require(size == measuredSize) { "The audiobook changed while it was being read." }

        val retriever = MediaMetadataRetriever()
        var embeddedTitle: String? = null
        var embeddedAuthor: String? = null
        var embeddedSeries: String? = null
        var embeddedCover: ByteArray? = null
        val duration = runCatching {
            // MediaMetadataRetriever is more reliable with a direct path for the
            // file:// URI used by website companion transfers. The content-resolver
            // overload remains necessary for document-provider imports.
            if (uri.scheme.equals("file", ignoreCase = true) && !uri.path.isNullOrBlank()) {
                retriever.setDataSource(uri.path)
            } else {
                retriever.setDataSource(context, uri)
            }
            embeddedTitle = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_TITLE)
            embeddedAuthor = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_ARTIST)
                ?: retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_AUTHOR)
            embeddedSeries = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_ALBUM)
            embeddedCover = retriever.embeddedPicture
            retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION)?.toDouble()?.div(1000.0)
        }.getOrNull()
        retriever.release()

        val extension = name.substringAfterLast('.', "audio").lowercase()
        val isMp4Family = extension in MP4_FAMILY_EXTENSIONS
        // MediaMetadataRetriever cannot reach freeform tags, which is where the
        // retail identifiers and the narrator live, so read the atoms directly.
        val tags = if (isMp4Family) Mp4TagReader(resolver).read(uri) else AudioEditionTags()

        // Anything inside the file outranks the filename. For a tagged M4B the
        // first two sources are the same `©nam` value, so this changes nothing for
        // files that were already identified correctly.
        val metadataTitle = tags.title?.trim()?.takeIf { it.isNotBlank() }
            ?: embeddedTitle?.trim()?.takeIf { it.isNotBlank() }
        val title = metadataTitle
            ?: EditionTitleCleaner.clean(name)
            ?: "Imported audiobook"

        InspectedAudio(
            fingerprint = BookFingerprint(
                sha256 = digest.digest().joinToString("") { "%02X".format(it) },
                fileSize = size,
                duration = duration,
                fileType = extension,
                workTitle = title,
                author = tags.author?.trim()?.takeIf { it.isNotBlank() }
                    ?: embeddedAuthor?.trim()?.takeIf { it.isNotBlank() },
                seriesTitle = tags.seriesTitle?.trim()?.takeIf { it.isNotBlank() }
                    ?: embeddedSeries?.trim()?.takeIf { it.isNotBlank() },
                seriesNumber = tags.seriesPart,
            ),
            fileName = name,
            title = title,
            contentType = resolver.getType(uri) ?: "audio/$extension",
            chapters = if (isMp4Family)
                Mp4ChapterReader(resolver).read(uri, duration) else emptyList(),
            coverBytes = embeddedCover,
            tags = tags,
            isTitleFromMetadata = metadataTitle != null,
        )
    }
}
