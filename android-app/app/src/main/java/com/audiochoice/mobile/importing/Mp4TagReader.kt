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
class Mp4TagReader(private val resolver: ContentResolver) {

    fun read(uri: Uri): AudioEditionTags = runCatching {
        resolver.openFileDescriptor(uri, "r")?.use { descriptor ->
            FileInputStream(descriptor.fileDescriptor).channel.use { channel ->
                findIlst(channel, 0, channel.size())
                    ?.let(Mp4TagParser::parseIlst)
                    ?: AudioEditionTags()
            }
        } ?: AudioEditionTags()
    }.getOrDefault(AudioEditionTags())

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

    private fun readBytes(channel: FileChannel, position: Long, size: Int): ByteArray? =
        read(channel, position, size)?.let { ByteArray(size).also(it::get) }

    private companion object {
        val CONTAINER_ATOMS = setOf("moov", "udta", "meta")

        /**
         * The metadata list also holds embedded artwork, so it is not tiny. This
         * bound keeps a corrupt or hostile size field from turning into a huge
         * allocation while still admitting real audiobook covers.
         */
        const val MAXIMUM_ILST_BYTES = 8L * 1024 * 1024
    }
}
