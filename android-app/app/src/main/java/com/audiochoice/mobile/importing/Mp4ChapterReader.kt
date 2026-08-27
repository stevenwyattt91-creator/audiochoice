package com.audiochoice.mobile.importing

import android.content.ContentResolver
import android.net.Uri
import com.audiochoice.mobile.data.AudioChapter
import java.io.FileInputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.channels.FileChannel

/** Reads the standard Nero `chpl` metadata used by many M4B/M4A audiobook files. */
class Mp4ChapterReader(private val resolver: ContentResolver) {
    fun read(uri: Uri, durationSeconds: Double?): List<AudioChapter> = runCatching {
        resolver.openFileDescriptor(uri, "r")?.use { descriptor ->
            FileInputStream(descriptor.fileDescriptor).channel.use { channel ->
                findChapterPayload(channel, 0, channel.size())?.let { parse(it, durationSeconds) }.orEmpty()
            }
        }.orEmpty()
    }.getOrDefault(emptyList())

    private fun findChapterPayload(channel: FileChannel, start: Long, end: Long): ByteArray? {
        var position = start
        while (position + 8 <= end) {
            val header = read(channel, position, 16) ?: return null
            var size = header.int.toLong() and 0xFFFFFFFFL
            val typeBytes = ByteArray(4).also(header::get)
            val type = String(typeBytes, Charsets.US_ASCII)
            var headerSize = 8L
            if (size == 1L) { size = header.long; headerSize = 16L }
            if (size == 0L) size = end - position
            if (size < headerSize || position + size > end) return null
            val payloadStart = position + headerSize
            val payloadSize = size - headerSize
            if (type == "chpl" && payloadSize in 6..1_048_576) {
                return readBytes(channel, payloadStart, payloadSize.toInt())
            }
            if (type in setOf("moov", "udta", "meta")) {
                // `meta` is a FullBox: its first four payload bytes are version and flags,
                // not another atom header. Some Audible/M4B files place chapters here.
                val childStart = if (type == "meta") payloadStart + 4 else payloadStart
                findChapterPayload(channel, childStart, position + size)?.let { return it }
            }
            position += size
        }
        return null
    }

    private fun parse(payload: ByteArray, durationSeconds: Double?): List<AudioChapter> {
        for (countOffset in listOf(4, 8)) {
            if (payload.size <= countOffset) continue
            val count = payload[countOffset].toInt() and 0xFF
            if (count == 0) continue
            var position = countOffset + 1
            val starts = mutableListOf<Pair<String, Double>>()
            repeat(count) { index ->
                if (position + 9 > payload.size) return@repeat
                val timestamp = ByteBuffer.wrap(payload, position, 8).order(ByteOrder.BIG_ENDIAN).long
                position += 8
                val titleLength = payload[position++].toInt() and 0xFF
                if (position + titleLength > payload.size) return@repeat
                val title = String(payload, position, titleLength, Charsets.UTF_8).ifBlank { "Chapter ${index + 1}" }
                position += titleLength
                starts += title to timestamp / 10_000_000.0
            }
            if (starts.size == count && starts.zipWithNext().all { it.first.second <= it.second.second }) {
                return starts.mapIndexed { index, value ->
                    AudioChapter(
                        title = value.first,
                        startSeconds = value.second,
                        endSeconds = starts.getOrNull(index + 1)?.second ?: durationSeconds ?: value.second,
                    )
                }
            }
        }
        return emptyList()
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
}

/** Containers whose chapter and `ilst` tag atoms AudioChoice can read directly. */
internal val MP4_FAMILY_EXTENSIONS = setOf("m4b", "m4a", "mp4", "aax", "aaxc")
