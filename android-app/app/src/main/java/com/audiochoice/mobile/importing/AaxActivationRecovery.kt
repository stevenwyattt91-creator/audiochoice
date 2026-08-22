package com.audiochoice.mobile.importing

import android.content.ContentResolver
import android.net.Uri
import java.security.MessageDigest
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

object AaxActivationVerifier {
    private val fixedKey = hex("77214d4b196a87cd520045fd20a51d67")

    fun calculatedChecksum(activationValue: UInt): ByteArray {
        val activation = byteArrayOf(
            (activationValue shr 24).toByte(),
            (activationValue shr 16).toByte(),
            (activationValue shr 8).toByte(),
            activationValue.toByte(),
        )
        val sha1 = MessageDigest.getInstance("SHA-1")
        val intermediateKey = sha1.digest(fixedKey + activation)
        val intermediateIv = sha1.digest(fixedKey + intermediateKey + activation)
        return sha1.digest(intermediateKey.copyOf(16) + intermediateIv.copyOf(16))
    }

    fun matches(activationValue: UInt, fileChecksum: ByteArray): Boolean =
        fileChecksum.size == 20 && MessageDigest.isEqual(
            calculatedChecksum(activationValue),
            fileChecksum,
        )

    fun hex(value: String): ByteArray {
        require(value.length % 2 == 0)
        return ByteArray(value.length / 2) { index ->
            value.substring(index * 2, index * 2 + 2).toInt(16).toByte()
        }
    }
}

class AaxChecksumReader(private val resolver: ContentResolver) {
    suspend fun read(uri: Uri): ByteArray = withContext(Dispatchers.IO) {
        resolver.openInputStream(uri).use { input ->
            requireNotNull(input) { "The selected AAX file could not be opened." }
            val marker = byteArrayOf('a'.code.toByte(), 'd'.code.toByte(), 'r'.code.toByte(), 'm'.code.toByte())
            var matched = 0
            var inspected = 0L
            while (inspected < MAX_HEADER_BYTES) {
                val value = input.read()
                require(value >= 0) { "The AAX authorization header was not found." }
                inspected++
                matched = if (value.toByte() == marker[matched]) matched + 1
                    else if (value.toByte() == marker[0]) 1 else 0
                if (matched == marker.size) {
                    var remaining = CHECKSUM_OFFSET_IN_PAYLOAD
                    while (remaining > 0) {
                        val skipped = input.skip(remaining.toLong()).toInt()
                        if (skipped > 0) remaining -= skipped
                        else {
                            require(input.read() >= 0) { "The AAX authorization header is incomplete." }
                            remaining--
                        }
                    }
                    return@withContext ByteArray(20).also { checksum ->
                        var offset = 0
                        while (offset < checksum.size) {
                            val count = input.read(checksum, offset, checksum.size - offset)
                            require(count > 0) { "The AAX checksum is incomplete." }
                            offset += count
                        }
                    }
                }
            }
            error("The selected file does not contain a supported AAX authorization header.")
        }
    }

    private companion object {
        const val CHECKSUM_OFFSET_IN_PAYLOAD = 68
        const val MAX_HEADER_BYTES = 32L * 1024 * 1024
    }
}

class CleanRoomAaxRecoveryEngine {
    suspend fun searchRange(
        fileChecksum: ByteArray,
        startInclusive: UInt,
        endInclusive: UInt,
        isCancelled: () -> Boolean = { false },
        onCheckpoint: (nextCandidate: UInt, progress: Float) -> Unit = { _, _ -> },
    ): UInt? = withContext(Dispatchers.Default) {
        require(fileChecksum.size == 20)
        require(startInclusive <= endInclusive)
        val start = startInclusive.toLong()
        val end = endInclusive.toLong()
        val count = end - start + 1
        var candidate = start
        while (candidate <= end) {
            if (isCancelled()) return@withContext null
            val value = candidate.toUInt()
            if (AaxActivationVerifier.matches(value, fileChecksum)) return@withContext value
            candidate++
            if ((candidate - start) % CHECKPOINT_INTERVAL == 0L) {
                onCheckpoint(candidate.toUInt(), ((candidate - start).toDouble() / count).toFloat())
            }
        }
        null
    }

    private companion object { const val CHECKPOINT_INTERVAL = 100_000L }
}
