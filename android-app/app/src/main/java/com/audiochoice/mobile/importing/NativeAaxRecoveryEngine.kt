package com.audiochoice.mobile.importing

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import kotlin.coroutines.coroutineContext

data class AaxRecoveryCheckpoint(
    val nextCandidate: UInt,
    val completedCandidates: ULong,
    val totalCandidates: ULong,
) {
    val progress: Float
        get() = if (totalCandidates == 0uL) 1f
        else (completedCandidates.toDouble() / totalCandidates.toDouble()).toFloat()
}

class NativeAaxRecoveryEngine {
    val available: Boolean get() = libraryLoaded

    suspend fun searchRange(
        fileChecksum: ByteArray,
        startInclusive: UInt,
        endInclusive: UInt,
        threads: Int = Runtime.getRuntime().availableProcessors().coerceAtLeast(1),
    ): UInt? = withContext(Dispatchers.Default) {
        check(libraryLoaded) { "The native AAX recovery engine is unavailable." }
        require(fileChecksum.size == 20)
        require(startInclusive <= endInclusive)
        nativeSearchRange(
            fileChecksum,
            startInclusive.toLong(),
            endInclusive.toLong(),
            threads.coerceIn(1, 32),
        ).takeIf { it >= 0 }?.toUInt()
    }

    suspend fun searchWithCheckpoints(
        fileChecksum: ByteArray,
        startInclusive: UInt = UInt.MIN_VALUE,
        endInclusive: UInt = UInt.MAX_VALUE,
        chunkSize: UInt = DEFAULT_CHUNK_SIZE,
        threads: Int = Runtime.getRuntime().availableProcessors().coerceAtLeast(1),
        onCheckpoint: suspend (AaxRecoveryCheckpoint) -> Unit = {},
    ): UInt? {
        require(fileChecksum.size == 20)
        require(startInclusive <= endInclusive)
        require(chunkSize > 0u)

        val start = startInclusive.toULong()
        val end = endInclusive.toULong()
        val total = end - start + 1uL
        var next = start
        while (next <= end) {
            coroutineContext.ensureActive()
            val chunkEnd = minOf(end, next + chunkSize.toULong() - 1uL)
            searchRange(fileChecksum, next.toUInt(), chunkEnd.toUInt(), threads)?.let { return it }
            next = chunkEnd + 1uL
            onCheckpoint(
                AaxRecoveryCheckpoint(
                    nextCandidate = if (next <= UInt.MAX_VALUE.toULong()) next.toUInt() else UInt.MAX_VALUE,
                    completedCandidates = next - start,
                    totalCandidates = total,
                ),
            )
        }
        return null
    }

    private external fun nativeSearchRange(
        checksum: ByteArray,
        startInclusive: Long,
        endInclusive: Long,
        threads: Int,
    ): Long

    private companion object {
        const val DEFAULT_CHUNK_SIZE: UInt = 1_000_000u
        val libraryLoaded = runCatching { System.loadLibrary("audiochoice_aax") }.isSuccess
    }
}
