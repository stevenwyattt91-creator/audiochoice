package com.audiochoice.contracts

import kotlinx.serialization.Serializable

/**
 * Whether a referral code names an active affiliate, checked from the sign-up screen before an
 * account is created.
 *
 * Deliberately thin -- true or false only. Nothing here identifies which affiliate a code belongs
 * to, so entering a stranger's code cannot be used to learn anything about them.
 */
@Serializable
data class ReferralCodeCheck(val valid: Boolean = false)
