package com.audiochoice.mobile.data

import com.audiochoice.contracts.BookFingerprint
import kotlinx.serialization.Serializable

@Serializable
data class ConversionConsentRequest(
    val fingerprint: BookFingerprint,
    val sourceFileName: String,
    val agreementVersion: String,
    val agreementText: String,
)

@Serializable
data class ConversionConsentRecord(
    val id: String,
    val userID: String,
    val userEmail: String,
    val userDisplayName: String,
    val fingerprint: BookFingerprint,
    val sourceFileName: String,
    val agreementVersion: String,
    val agreementText: String,
    val acceptedAt: String,
)
