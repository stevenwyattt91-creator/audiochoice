package com.audiochoice.contracts

import kotlinx.serialization.Serializable

/**
 * What the account is entitled to, as the server sees it.
 *
 * The only source of a premium narration tier. Deliberately not combined with any local
 * purchase state: a receipt on the device says a payment happened, not that it cleared, was
 * not refunded, and belongs to the account now signed in. Deriving entitlement from the
 * server alone means a lapsed or refunded subscription stops working, and a device with a
 * stale receipt cannot mint premium synthesis for itself.
 */
@Serializable
data class AccountAccessResponse(
    val isActive: Boolean = false,
    val plan: String = "",
    val source: String = "",
    /**
     * When access lapses, or null for access with no end date.
     *
     * ISO-8601, as the server writes it. Parsed rather than trusted: a value that cannot be
     * read is treated as expired rather than as absent, because "absent" grants unlimited
     * access and a parse failure is not evidence of that.
     */
    val expiresAt: String? = null,
    val canUseFilters: Boolean = false,
    val canUseCompanion: Boolean = false,
)
