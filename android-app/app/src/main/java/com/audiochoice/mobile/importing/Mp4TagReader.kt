package com.audiochoice.mobile.importing

import android.content.ContentResolver
import android.net.Uri
import java.io.FileInputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.channels.FileChannel

/**
 * Locates the `moov/udta/meta/ilst` metadata list in an MP4-family container and
 * hands it to [Mp4TagParser].
 *
 * Android's MediaMetadataRetriever only surfaces a fixed handful of fields and has
 * no way to reach freeform tags, which is exactly where retail identifiers such as
 * ASIN and ISBN are stored. Reading the atoms directly is the only way to get them.
 */
/** What the container itself can tell us, independent of MediaMetadataRetriever. */
data class ContainerMetadata(
    val tags: AudioEditionTags = AudioEditionTags(),
    val coverBytes: ByteArray? = null,
    val durationSeconds: Double? = null,
) {
    // ByteArray uses reference equality, which would make two identical results unequal.
    override fun equals(other: Any?): Boolean =
        this === other ||
            (other is ContainerMetadata &&
                tags == other.tags &&
                durationSeconds == other.durationSeconds &&
                coverBytes.contentEquals(other.coverBytes))

    override fun hashCode(): Int {
        var result = tags.hashCode()
        result = 31 * result + (coverBytes?.contentHashCode() ?: 0)
        result = 31 * result + (durationSeconds?.hashCode() ?: 0)
        return result
    }
}

class Mp4TagReader(private val resolver: ContentResolver) {

    fun read(uri: Uri): AudioEditionTags = readContainer(uri).tags

    /**
     * Everything this reader can get from the container in one pass.
     *
     * Duration and artwork used to come only from MediaMetadataRetriever, which needs a
     * seekable data source and returns nothing when it cannot open one. This reader opens a
     * file descriptor and seeks, which is why it keeps working on files the retriever gives up
     * on — so it now supplies both as well, and the retriever becomes a fallback rather than
     * the only source.
     */
    fun readContainer(uri: Uri): ContainerMetadata = runCatching {
        resolver.openFileDescriptor(uri, "r")?.use { descriptor ->
            FileInputStream(descriptor.fileDescriptor).channel.use { channel ->
                val ilst = findIlst(channel, 0, channel.size())
                ContainerMetadata(
                    tags = ilst?.let(Mp4TagParser::parseIlst) ?: AudioEditionTags(),
                    coverBytes = ilst?.let(Mp4TagParser::coverArt),
                    durationSeconds = findDurationSeconds(channel, 0, channel.size()),
                )
            }
        } ?: ContainerMetadata()
    }.getOrDefault(ContainerMetadata())

    /**
     * Reads the runtime from `moov/mvhd`, which every MP4-family file has.
     *
     * The movie header states a timescale and a duration in those units, so this is the
     * container's own answer rather than an estimate.
     */
    private fun findDurationSeconds(channel: FileChannel, start: Long, end: Long): Double? {
        var position = start
        while (position + 8 <= end) {
            val header = read(channel, position, 16) ?: return null
            var size = header.int.toLong() and 0xFFFFFFFFL
            val typeBytes = ByteArray(4).also(header::get)
            val type = String(typeBytes, Charsets.US_ASCII)
            var headerSize = 8L
            if (size == 1L) {
                size = header.long
                headerSize = 16L
            }
            if (size == 0L) size = end - position
            if (size < headerSize || position + size > end) return null
            val payloadStart = position + headerSize

            if (type == "mvhd" && payloadSizeOf(size, headerSize) in 1..MAXIMUM_MVHD_BYTES) {
                // Locating the atom is this class's job; reading it belongs with the rest of
                // the container parsing, where it can be tested without a file.
                return readBytes(channel, payloadStart, (size - headerSize).toInt())
                    ?.let(Mp4TagParser::durationSeconds)
            }
            // `mvhd` sits directly inside `moov`, so only that needs descending into.
            if (type == "moov") {
                findDurationSeconds(channel, payloadStart, position + size)?.let { return it }
            }
            position += size
        }
        return null
    }

    private fun findIlst(channel: FileChannel, start: Long, end: Long): ByteArray? {
        var position = start
        while (position + 8 <= end) {
            val header = read(channel, position, 16) ?: return null
            var size = header.int.toLong() and 0xFFFFFFFFL
            val typeBytes = ByteArray(4).also(header::get)
            val type = String(typeBytes, Charsets.US_ASCII)
            var headerSize = 8L
            if (size == 1L) {
                size = header.long
                headerSize = 16L
            }
            if (size == 0L) size = end - position
            if (size < headerSize || position + size > end) return null
            val payloadStart = position + headerSize
            val payloadSize = size - headerSize

            if (type == "ilst" && payloadSize in 1..MAXIMUM_ILST_BYTES) {
                return readBytes(channel, payloadStart, payloadSize.toInt())
            }
            if (type in CONTAINER_ATOMS) {
                // `meta` is a FullBox: its first four payload bytes are version and
                // flags rather than a nested atom header. Same quirk the chapter
                // reader has to account for.
                val childStart = if (type == "meta") payloadStart + 4 else payloadStart
                findIlst(channel, childStart, position + size)?.let { return it }
            }
            position += size
        }
        return null
    }

    private fun read(channel: FileChannel, position: Long, size: Int): ByteBuffer? {
        val buffer = ByteBuffer.allocate(size).order(ByteOrder.BIG_ENDIAN)
        var offset = position
        while (buffer.hasRemaining()) {
            val count = channel.read(buffer, offset)
            if (count <= 0) return null
            offset += count
        }
        buffer.flip()
        return buffer
    }

    private fun payloadSizeOf(size: Long, headerSize: Long): Long = size - headerSize

    private fun readBytes(channel: FileChannel, position: Long, size: Int): ByteArray? =
        read(channel, position, size)?.let { ByteArray(size).also(it::get) }

    private companion object {
        val CONTAINER_ATOMS = setOf("moov", "udta", "meta")

        /**
         * The metadata list also holds embedded artwork, so it is not tiny. This
         * bound keeps a corrupt or hostile size field from turning into a huge
         * allocation while still admitting real audiobook covers.
         *
         * Raised from 8 MB because exceeding it loses the whole list, not just the artwork:
         * the title, author, narrator and retail identifiers all live in the same atom. One
         * high-resolution cover was enough to leave a book with no metadata at all.
         */
        const val MAXIMUM_ILST_BYTES = 24L * 1024 * 1024

        /** A movie header is a fixed hundred bytes or so; anything larger is not one. */
        const val MAXIMUM_MVHD_BYTES = 4096L
    }
}
