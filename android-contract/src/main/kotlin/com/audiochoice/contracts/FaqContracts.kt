package com.audiochoice.contracts

import kotlinx.serialization.Serializable

/**
 * The in-app help content, served rather than compiled into each app.
 *
 * Both apps used to hold their own hardcoded copy and they drifted: Android carried eleven questions
 * and iOS four different ones, so the same product answered differently depending on the phone.
 * Neither mentioned the reading edition, the two library shelves, the voice tiers, rescanning or
 * password reset.
 *
 * Serving it removes an App Store review from the path of correcting a wrong answer, which is what
 * let it go stale. Each app keeps a bundled copy as a fallback, because a help screen that is empty
 * when the network is poor is worse than one slightly behind.
 */
@Serializable
data class FaqResponse(
    /** Raised whenever the content changes, so a client can prefer the newer of two copies. */
    val version: Int = 0,
    val sections: List<FaqSection> = emptyList(),
)

@Serializable
data class FaqSection(val title: String = "", val items: List<FaqEntry> = emptyList())

@Serializable
data class FaqEntry(val question: String = "", val answer: String = "")
