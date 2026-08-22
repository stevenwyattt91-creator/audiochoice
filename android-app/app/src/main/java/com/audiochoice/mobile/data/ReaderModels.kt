package com.audiochoice.mobile.data

import kotlinx.serialization.Serializable

@Serializable
data class ReaderAlignmentRequest(
    val libraryBookID: String,
    val epubText: String,
)

@Serializable
data class ReaderAlignmentResponse(val ranges: List<ReaderTimingRange> = emptyList())

/** A private timing-to-character range. It intentionally contains no transcript text. */
@Serializable
data class ReaderTimingRange(
    val startTime: Double,
    val endTime: Double,
    val startCharacter: Int,
    val endCharacter: Int,
)
