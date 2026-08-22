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
            retriever.setDataSource(context, uri)
            embeddedTitle = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_TITLE)
            embeddedAuthor = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_ARTIST)
                ?: retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_AUTHOR)
            embeddedSeries = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_ALBUM)
            embeddedCover = retriever.embeddedPicture
            retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION)?.toDouble()?.div(1000.0)
        }.getOrNull()
        retriever.release()

        val extension = name.substringAfterLast('.', "audio").lowercase()
        val fileTitle = name.substringBeforeLast('.').replace('_', ' ').trim().ifBlank { "Imported audiobook" }
        val title = embeddedTitle?.trim()?.takeIf { it.isNotBlank() } ?: fileTitle
        InspectedAudio(
            fingerprint = BookFingerprint(
                sha256 = digest.digest().joinToString("") { "%02X".format(it) },
                fileSize = size,
                duration = duration,
                fileType = extension,
                workTitle = title,
                author = embeddedAuthor?.trim()?.takeIf { it.isNotBlank() },
                seriesTitle = embeddedSeries?.trim()?.takeIf { it.isNotBlank() },
            ),
            fileName = name,
            title = title,
            contentType = resolver.getType(uri) ?: "audio/$extension",
            chapters = if (extension in setOf("m4b", "m4a", "mp4", "aax"))
                Mp4ChapterReader(resolver).read(uri, duration) else emptyList(),
            coverBytes = embeddedCover,
        )
    }
}
