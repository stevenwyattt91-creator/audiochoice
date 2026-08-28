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
    /**
     * The fingerprint of the file actually on this device, when the row above
     * adopts a different one. Lets the server link the two rather than losing
     * track of artifacts stored under the local file's hash.
     */
    val sourceFingerprint: BookFingerprint? = null,
    /** Identity evidence read from the file's own tags and chapter marks. */
    val signature: EditionSignature? = null,
    /**
     * The publisher's synopsis, as carried by this file's own description tags.
     *
     * Stored against the edition, so a well-tagged copy gives every other owner of that
     * recording a real description in Explore instead of none.
     */
    val description: String? = null,
)

/**
 * Corrects a book's display details.
 *
 * Display only: these values deliberately do not feed edition identification, which
 * works from the file's own metadata rather than typed-in text.
 */
@Serializable
data class LibraryBookDetailsRequest(
    val title: String,
    val author: String? = null,
    val narrator: String? = null,
)

/**
 * Reports identity evidence for a book already in the library.
 *
 * Books imported before signatures existed carry none, so they get no benefit from
 * edition matching. This supplies it afterwards without rewriting the row's title,
 * author or artwork.
 */
@Serializable
data class EditionSignatureReportRequest(
    val fingerprint: BookFingerprint,
    val signature: EditionSignature,
    val sourceFingerprint: BookFingerprint? = null,
)

/**
 * Reports the synopsis carried by a file already in the library.
 *
 * Separate from the upsert because that overwrites the stored title, which would revert a
 * correction on a book the listener had already renamed.
 */
@Serializable
data class EditionDescriptionReportRequest(
    val fingerprint: BookFingerprint,
    val description: String,
)

/**
 * Identity evidence about a recording that a file's byte hash cannot express.
 *
 * Only the client can read this, since the server never sees container tags. It is
 * what lets a converted copy be recognised as the same recording, and a matching
 * retail product identifier is the one signal strong enough to reuse filter results.
 */
@Serializable
data class EditionSignature(
    val productIdentifier: String? = null,
    val narrator: String? = null,
    /** Chapter start offsets in whole seconds; survives re-encoding. */
    val chapterOffsetSeconds: List<Int>? = null,
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
    /**
     * The retail product identifier for this recording, when one is known.
     *
     * Lets two copies of one recording be recognised as the same catalogue entry, which
     * titles cannot do on their own: the same edition arrives spelled several ways
     * depending on who tagged the file.
     */
    val productIdentifier: String? = null,
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
