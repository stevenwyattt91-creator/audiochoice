package com.audiochoice.contracts

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

@Serializable
enum class CloudScanStatus {
    @SerialName("available") AVAILABLE,
    @SerialName("uploadRequired") UPLOAD_REQUIRED,
    @SerialName("queued") QUEUED,
    @SerialName("processing") PROCESSING,
    @SerialName("completed") COMPLETED,
    @SerialName("failed") FAILED
}

@Serializable
data class BookFingerprint(
    val version: Int = 1,
    val sha256: String,
    val fileSize: Long,
    val duration: Double? = null,
    val fileType: String,
    val workTitle: String? = null,
    val author: String? = null,
    val seriesTitle: String? = null,
    val seriesNumber: Int? = null,
    val editionType: String? = null,
    val partNumber: Int? = null,
    val totalParts: Int? = null
)

@Serializable
data class ScanEvent(
    val id: String,
    val startTime: Double,
    val endTime: Double,
    val categoryID: String,
    val groupID: String,
    val eventID: String,
    val confidence: Double,
    val stableKey: String = "",
    val safeDescription: String = "Content event detected",
    val aggregateKey: String? = null,
    val aggregateDisplay: String? = null,
)

@Serializable
data class ScanResult(
    val events: List<ScanEvent>,
    val scanDate: String,
    val scannerVersion: String
)

@Serializable
data class CloudScanRequest(
    val fingerprint: BookFingerprint,
    val currentScannerVersion: String? = null
)

@Serializable
data class CloudScanResponse(
    val status: CloudScanStatus,
    val scanID: String? = null,
    val result: ScanResult? = null,
    val taxonomyVersion: String? = null,
    val progressPercent: Int = 0,
    val progressStage: String? = null,
    val completedChunks: Int = 0,
    val totalChunks: Int = 0,
    val percentComplete: Int = 0,
)

@Serializable
data class CloudUploadAuthorizationRequest(
    val fingerprint: BookFingerprint,
    val fileName: String,
    val contentType: String,
    val fileSize: Long
)

@Serializable
data class CloudUploadAuthorizationResponse(
    val uploadID: String,
    val uploadURL: String,
    val method: String,
    val headers: Map<String, String>,
    val expiresAt: String
)

@Serializable
data class CloudScanJobSubmissionRequest(
    val uploadID: String,
    val fingerprint: BookFingerprint
)

@Serializable
data class CompanionTransferClaimResponse(
    val transferID: String,
    val fileName: String,
    val contentType: String,
    val fileSize: Long,
    val sha256: String,
    val downloadURL: String,
    val expiresAt: String,
)

object ContentTaxonomy {
    const val VERSION = "2.0"
    fun supports(event: ScanEvent): Boolean = event.categoryID.isNotBlank() &&
        event.groupID.isNotBlank() && event.eventID.isNotBlank()
}
