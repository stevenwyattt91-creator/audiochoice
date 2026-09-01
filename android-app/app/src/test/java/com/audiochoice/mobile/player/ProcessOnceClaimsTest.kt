package com.audiochoice.mobile.player

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The keying of the once-per-process guards.
 *
 * These pin the two mistakes that would each reintroduce the bug this type was written for:
 * a listener returning to a player Android had killed in the background and finding no book.
 */
class ProcessOnceClaimsTest {
    @Test
    fun `the first claim succeeds and the second does not`() {
        val claims = ProcessOnceClaims()
        assertTrue(claims.claim("restore", "user-1"))
        assertFalse(claims.claim("restore", "user-1"))
    }

    @Test
    fun `reconciling progress does not consume the claim for reopening the book`() {
        // Sharing one claim would mean hydration, which runs first, silently suppressed the
        // reopen -- which is exactly how the original bug behaved.
        val claims = ProcessOnceClaims()
        assertTrue(claims.claim("hydrate", "user-1"))
        assertTrue(claims.claim("restore", "user-1"))
    }

    @Test
    fun `a second account on the same device gets its own claims`() {
        val claims = ProcessOnceClaims()
        assertTrue(claims.claim("restore", "user-1"))
        assertTrue(claims.claim("restore", "user-2"))
        assertFalse(claims.claim("restore", "user-2"))
    }

    @Test
    fun `a user id containing the separator cannot collide with another`() {
        val claims = ProcessOnceClaims()
        assertTrue(claims.claim("restore", "a"))
        assertTrue(claims.claim("restorea", ""))
    }
}
