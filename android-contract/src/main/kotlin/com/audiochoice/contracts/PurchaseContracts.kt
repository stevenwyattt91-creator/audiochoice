package com.audiochoice.contracts

import kotlinx.serialization.Serializable

/**
 * An acknowledged Play Billing purchase submitted for server-side verification.
 *
 * The server looks the token up against the Play Developer API -- Google's own record of the
 * purchase -- rather than trusting anything else claimed here. [productID] is used only to route
 * and is re-checked against what that lookup says was actually purchased.
 */
@Serializable
data class GooglePurchaseRequest(val productID: String, val purchaseToken: String)
