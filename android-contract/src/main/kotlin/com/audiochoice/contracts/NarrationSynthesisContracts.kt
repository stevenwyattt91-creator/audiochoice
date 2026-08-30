package com.audiochoice.contracts

import kotlinx.serialization.Serializable

/** A voice the server offers, with a fixed pre-rendered sample. */
@Serializable
data class NarrationVoiceDescriptor(
    val voiceID: String = "",
    val displayName: String = "",
    val language: String = "",
    val provider: String = "",
    val sampleUrl: String = "",
)

/**
 * The voices available, with the agreement premium synthesis requires.
 *
 * The agreement travels with the voices so a listener is never shown a premium voice without the
 * statement that explains what choosing it does. Its text comes from the server rather than the
 * app so a wording change reaches every client at once.
 */
@Serializable
data class NarrationVoicesResponse(
    val voices: List<NarrationVoiceDescriptor> = emptyList(),
    val agreementVersion: String = "",
    val agreementText: String = "",
)

@Serializable
data class NarrationAcknowledgementRequest(
    val agreementVersion: String,
    val agreementText: String,
)

@Serializable
data class NarrationAcknowledgementResponse(
    val agreementVersion: String = "",
    val acceptedAt: String? = null,
)

/**
 * One chapter submitted for synthesis.
 *
 * [units] arrive with filtered characters already removed, so nothing a listener asked to have
 * filtered ever leaves the device. That is stronger than the audiobook path can manage, where the
 * recording already contains the passage and playback skips it.
 */
@Serializable
data class NarrationChapterRequest(
    val fingerprint: BookFingerprint,
    val chapterIndex: Int,
    val voiceID: String,
    val language: String? = null,
    val units: List<NarrationUnitRequest> = emptyList(),
) {
    /**
     * Prints its size rather than its text.
     *
     * A data class prints every property, so one log line holding this request would write a
     * chapter of a novel into logcat — the same disclosure the feature is careful to avoid,
     * arriving by accident.
     */
    override fun toString(): String =
        "NarrationChapterRequest(sha256=${fingerprint.sha256}, chapterIndex=$chapterIndex, " +
            "voiceID=$voiceID, units=${units.size}, " +
            "characters=${units.sumOf { it.text.length }})"
}

@Serializable
data class NarrationUnitRequest(
    val startCharacter: Int,
    val endCharacter: Int,
    val text: String,
)

@Serializable
data class NarrationChapterAccepted(
    val jobID: String = "",
    val status: String = "",
)

/**
 * A chapter job's state, and its audio once there is any.
 *
 * [audioBase64] carries the finished chapter rather than a link to it. Chapter audio is derived
 * closely enough from a listener's book that keeping it in cloud storage would be the same
 * disclosure the text handling avoids, so there is nowhere it is stored.
 *
 * The timings are **chapter-relative**, measured from the first sample of this chapter's own
 * audio. Book_Time is applied on the device by adding the chapter's cumulative start, which is
 * what lets one chapter be re-rendered without invalidating any other chapter's timings.
 */
@Serializable
data class NarrationChapterStatus(
    val jobID: String = "",
    val chapterIndex: Int = 0,
    val status: String = "",
    val provider: String? = null,
    val modelVersion: String? = null,
    val voiceID: String? = null,
    val durationSeconds: Double = 0.0,
    val timings: List<NarrationUnitTiming> = emptyList(),
    val audioBase64: String? = null,
    val error: String? = null,
) {
    val isCompleted: Boolean get() = status.equals("completed", ignoreCase = true)
    val isFailed: Boolean get() = status.equals("failed", ignoreCase = true)

    override fun toString(): String =
        "NarrationChapterStatus(jobID=$jobID, chapterIndex=$chapterIndex, status=$status, " +
            "provider=$provider, durationSeconds=$durationSeconds, timings=${timings.size}, " +
            "audioBytes=${audioBase64?.length ?: 0})"
}

@Serializable
data class NarrationUnitTiming(
    val startCharacter: Int,
    val endCharacter: Int,
    val startSeconds: Double,
    val endSeconds: Double,
)
