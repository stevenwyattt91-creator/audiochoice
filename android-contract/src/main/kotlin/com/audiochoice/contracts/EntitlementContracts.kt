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
/**
 * Plan names the server sends in [AccountAccessResponse.plan].
 *
 * Kept beside the contract so both apps read the same spelling. Compared case-insensitively, because
 * a plan name travels through JSON and a database column and is too important to hinge on casing
 * surviving that.
 */
object AccountPlans {
    const val FREE = "free"

    /**
     * A beta tester, given full access at no charge permanently.
     *
     * Free rather than discounted on purpose. A reduced price would need a second subscription
     * product in both stores, logic choosing which to offer, and a server check that a cheap receipt
     * belongs to a real founder -- the product exists in the store whether or not the app shows it,
     * so without that check anyone could buy it. That is a permanent cost for a handful of accounts.
     */
    const val FOUNDER = "founder"

    /**
     * A verified, paying subscriber -- the Play Store's own subscription, not a separate product
     * this app prices. Only ever set by the server after Google's Play Developer API confirms the
     * purchase.
     */
    const val PREMIUM = "premium"

    /** Whether this plan means the account is never charged. */
    fun isComplimentary(plan: String?): Boolean = plan?.trim()?.equals(FOUNDER, ignoreCase = true) == true
}

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
