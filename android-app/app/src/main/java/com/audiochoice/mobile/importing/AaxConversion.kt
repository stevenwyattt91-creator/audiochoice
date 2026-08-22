package com.audiochoice.mobile.importing

import android.content.Context
import android.net.Uri
import java.time.Instant

data class AaxOwnershipAcceptance(
    val agreementVersion: String,
    val acceptedAt: Instant,
    val sourceFileName: String,
)

sealed interface AaxConversionResult {
    data class Converted(
        val uri: Uri,
        val fileName: String,
        val coverBytes: ByteArray? = null,
    ) : AaxConversionResult
    data class AuthorizationRequired(val message: String) : AaxConversionResult
}

interface AaxConverter {
    suspend fun convert(
        source: Uri,
        sourceFileName: String,
        acceptance: AaxOwnershipAcceptance,
        onProgress: (Float) -> Unit,
    ): AaxConversionResult
}

/**
 * The single device-side boundary for AAX conversion. Protected AAX data must never be sent
 * to the AudioChoice server. An attorney-approved authorization provider can be added here
 * without creating a second conversion path in the UI or import pipeline.
 */
class LocalAaxConverter(private val context: Context) : AaxConverter {
    override suspend fun convert(
        source: Uri,
        sourceFileName: String,
        acceptance: AaxOwnershipAcceptance,
        onProgress: (Float) -> Unit,
    ): AaxConversionResult {
        require(acceptance.agreementVersion == AGREEMENT_VERSION)
        onProgress(0f)
        val checksum = AaxChecksumReader(context.contentResolver).read(source)
        val checksumKey = checksum.joinToString("") { "%02x".format(it.toInt() and 0xff) }
        val store = AaxRecoveryStore(context)
        var activation = store.activation(checksumKey)
        if (activation == null) {
            val engine = NativeAaxRecoveryEngine()
            check(engine.available) { "The local AAX recovery component is unavailable on this device." }
            val resumeAt = store.checkpoint(checksumKey)
            val remaining = UInt.MAX_VALUE.toULong() - resumeAt.toULong() + 1uL
            activation = engine.searchWithCheckpoints(
                fileChecksum = checksum,
                startInclusive = resumeAt,
                onCheckpoint = { checkpoint ->
                    store.saveCheckpoint(checksumKey, checkpoint.nextCandidate)
                    val completedSinceResume = checkpoint.completedCandidates
                    val overallCompleted = resumeAt.toULong() + completedSinceResume
                    onProgress((overallCompleted.toDouble() / FULL_RANGE_SIZE.toDouble()).toFloat() * RECOVERY_WEIGHT)
                },
            )
            if (activation == null && resumeAt != UInt.MIN_VALUE) {
                store.clearCheckpoint(checksumKey)
                activation = engine.searchWithCheckpoints(
                    fileChecksum = checksum,
                    endInclusive = resumeAt - 1u,
                    onCheckpoint = { checkpoint ->
                        store.saveCheckpoint(checksumKey, checkpoint.nextCandidate)
                        val recoveryProgress = (remaining + checkpoint.completedCandidates).toDouble()
                            .div(FULL_RANGE_SIZE.toDouble()).toFloat()
                        onProgress(recoveryProgress * RECOVERY_WEIGHT)
                    },
                )
            }
            val recovered = requireNotNull(activation) {
                "AudioChoice could not recover local authorization for this AAX file."
            }
            store.saveActivation(checksumKey, recovered)
        }
        onProgress(RECOVERY_WEIGHT)
        val readyActivation = requireNotNull(activation) { "Local AAX authorization was not available." }
        val converted = NativeAaxRemuxer(context).remux(source, sourceFileName, readyActivation)
        onProgress(1f)
        return converted
    }

    companion object {
        const val AGREEMENT_VERSION = "2026-08-06"
        const val AGREEMENT_TEXT = "By continuing, I confirm that I legally acquired this audiobook " +
            "and have the right to convert it for my personal use. I will not use AudioChoice to " +
            "copy, share, distribute, sell, or process content I do not lawfully own or control."
        private const val FULL_RANGE_SIZE = 4_294_967_296uL
        private const val RECOVERY_WEIGHT = 0.7f
    }
}
