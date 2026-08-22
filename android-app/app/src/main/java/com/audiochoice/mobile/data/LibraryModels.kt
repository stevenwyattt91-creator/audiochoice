package com.audiochoice.mobile.data

import com.audiochoice.contracts.BookFingerprint
import kotlinx.serialization.Serializable

@Serializable
data class LibraryBookUpsertRequest(
    val fingerprint: BookFingerprint,
    val title: String,
    val author: String? = null,
    val narrator: String? = null,
    val coverImageURL: String? = null,
    val coverImageBase64: String? = null,
    val coverImageContentType: String? = null,
)

@Serializable
data class EmbeddedCoverUploadRequest(
    val fingerprint: BookFingerprint,
    val contentType: String,
    val base64Data: String,
)

@Serializable
data class LibraryBook(
    val id: String,
    val fingerprint: BookFingerprint,
    val title: String,
    val author: String? = null,
    val narrator: String? = null,
    val coverImageURL: String? = null,
    val playbackPositionSeconds: Double = 0.0,
    val isFinished: Boolean = false,
    val isFavorite: Boolean = false,
    val addedAt: String,
    val updatedAt: String,
)

@Serializable
data class PlaybackProgressRequest(val positionSeconds: Double, val isFinished: Boolean)

@Serializable
data class BookmarkCreateRequest(val positionSeconds: Double, val title: String? = null, val note: String? = null)

@Serializable
data class LibraryBookmark(
    val id: String,
    val libraryBookID: String,
    val positionSeconds: Double,
    val title: String? = null,
    val note: String? = null,
    val createdAt: String,
    val updatedAt: String,
)

@Serializable
data class ExploreCatalogBook(
    val catalogID: String,
    val title: String,
    val author: String? = null,
    val seriesTitle: String? = null,
    val seriesNumber: Int? = null,
    val editionType: String? = null,
    val duration: Double? = null,
    val fileType: String,
    val scanDate: String,
    val scannerVersion: String,
    val eventCount: Int,
    val detectedGroupIDs: List<String>,
    val coverImageURL: String? = null,
    val description: String? = null,
    val purchaseURL: String,
    val purchaseProvider: String,
    val purchaseVerified: Boolean = false,
)

@Serializable
data class BookFilterSettingsUpsertRequest(
    val disabledCategoryIDs: List<String>,
    val disabledGroupIDs: List<String>,
    val disabledEventKeys: List<String>,
    val disabledAggregateKeys: List<String>,
)

@Serializable
data class BookFilterSettings(
    val libraryBookID: String,
    val disabledCategoryIDs: List<String> = emptyList(),
    val disabledGroupIDs: List<String> = emptyList(),
    val disabledEventKeys: List<String> = emptyList(),
    val disabledAggregateKeys: List<String> = emptyList(),
    val updatedAt: String,
)
