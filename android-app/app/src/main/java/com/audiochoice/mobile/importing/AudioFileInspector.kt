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

        val extension = name.substringAfterLast('.', "audio").lowercase()
        val isMp4Family = extension in MP4_FAMILY_EXTENSIONS
        // MediaMetadataRetriever cannot reach freeform tags, which is where the
        // retail identifiers and the narrator live, so read the atoms directly. This also
        // supplies the runtime and the artwork, because it opens a seekable descriptor and
        // therefore keeps working on files the retriever cannot open at all.
        val container = if (isMp4Family) {
            Mp4TagReader(resolver).readContainer(uri)
        } else {
            ContainerMetadata()
        }
        val tags = container.tags

        val retriever = MediaMetadataRetriever()
        var embeddedTitle: String? = null
        var embeddedAuthor: String? = null
        var embeddedSeries: String? = null
        var embeddedCover: ByteArray? = null
        var retrieverDuration: Double? = null
        // Each field is read on its own. All five used to sit inside one runCatching, so a
        // failure anywhere -- most often opening the data source -- silently abandoned every
        // field after it. That is how a book arrived with a title, an author, a narrator and
        // chapters but no runtime and no cover: those four come from the atom reader above,
        // and everything the retriever would have supplied was lost together.
        val opened = runCatching {
            // A seekable descriptor first, matching what the atom reader uses. The
            // content-resolver overload fails on providers that only hand back a stream, and
            // the retriever needs random access.
            when {
                uri.scheme.equals("file", ignoreCase = true) && !uri.path.isNullOrBlank() ->
                    retriever.setDataSource(uri.path)
                else -> resolver.openFileDescriptor(uri, "r")?.use { descriptor ->
                    retriever.setDataSource(descriptor.fileDescriptor)
                } ?: retriever.setDataSource(context, uri)
            }
        }.isSuccess

        if (opened) {
            embeddedTitle = runCatching {
                retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_TITLE)
            }.getOrNull()
            embeddedAuthor = runCatching {
                retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_ARTIST)
                    ?: retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_AUTHOR)
            }.getOrNull()
            embeddedSeries = runCatching {
                retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_ALBUM)
            }.getOrNull()
            embeddedCover = runCatching { retriever.embeddedPicture }.getOrNull()
            retrieverDuration = runCatching {
                retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION)
                    ?.toDouble()?.div(1000.0)
            }.getOrNull()
        }
        runCatching { retriever.release() }

        // The container's own movie header is authoritative and cheap, so it is preferred and
        // the retriever fills in for formats this reader does not parse.
        val duration = container.durationSeconds?.takeIf { it > 0 }
            ?: retrieverDuration?.takeIf { it > 0 }
        val coverBytes = container.coverBytes ?: embeddedCover

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
            coverBytes = coverBytes,
            tags = tags,
            isTitleFromMetadata = metadataTitle != null,
        )
    }
}
