package com.audiochoice.mobile.player

/**
 * Tracks work that must happen once per process rather than once ever.
 *
 * Held by PlayerViewModel so it inherits that object's lifetime: retained across a
 * configuration change, gone when the process is recreated. That is the lifetime both
 * callers want, and it is the whole point of this type existing.
 *
 * The guard it replaced was a `rememberSaveable` flag in the UI, which has the opposite
 * lifetime -- it is written into the Activity's saved-state bundle and restored after the
 * process is killed. Reopening the last book sat behind that flag, so when Android reaped a
 * backgrounded app the flag came back saying "already done" and the book was never reopened,
 * while the Player tab, restored from the same bundle, had nothing to show. Extracted here
 * so the keying can be pinned by tests.
 */
class ProcessOnceClaims {
    private val claimed = mutableSetOf<String>()

    /**
     * Claims [purpose] for [userID], returning true only for the first caller.
     *
     * Keyed on both parts on purpose. Sharing one claim between purposes would let whichever
     * ran first suppress the other, and keying on purpose alone would carry one account's
     * completed work over to the next account signed in on the device.
     */
    fun claim(purpose: String, userID: String): Boolean = claimed.add("$purpose\u0000$userID")
}
