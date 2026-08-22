package com.audiochoice.mobile.data

import kotlinx.serialization.Serializable

@Serializable
data class AudioChapter(val title: String, val startSeconds: Double, val endSeconds: Double)
