package com.audiochoice.mobile.player

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * The remaining figure shown under the scrubber.
 *
 * It is the only number in the app a listener uses to decide whether they can finish before
 * they have to be somewhere, so each speed the player offers is pinned rather than trusting
 * one division to cover them all.
 */
class ListeningTimeTest {
    @Test
    fun `normal speed leaves the book's own remaining length untouched`() {
        assertEquals(3_600_000L, ListeningTime.remainingRealMs(3_600_000L, 1f))
    }

    @Test
    fun `an hour of audio takes forty eight minutes at one and a quarter`() {
        assertEquals(2_880_000L, ListeningTime.remainingRealMs(3_600_000L, 1.25f))
    }

    @Test
    fun `an hour of audio takes forty minutes at one and a half`() {
        assertEquals(2_400_000L, ListeningTime.remainingRealMs(3_600_000L, 1.5f))
    }

    @Test
    fun `an hour of audio takes half an hour at double speed`() {
        assertEquals(1_800_000L, ListeningTime.remainingRealMs(3_600_000L, 2f))
    }

    @Test
    fun `slowing a narrator down leaves longer to listen, not less`() {
        assertEquals(4_800_000L, ListeningTime.remainingRealMs(3_600_000L, 0.75f))
    }

    @Test
    fun `a finished book has nothing left at any speed`() {
        assertEquals(0L, ListeningTime.remainingRealMs(0L, 2f))
        assertEquals(0L, ListeningTime.remainingRealMs(-5_000L, 1f))
    }

    @Test
    fun `a stored speed of zero reads as normal rather than dividing by it`() {
        // Nothing in the app can set this; a corrupted stored value must not report a book
        // that never ends.
        assertEquals(3_600_000L, ListeningTime.remainingRealMs(3_600_000L, 0f))
        assertEquals(3_600_000L, ListeningTime.remainingRealMs(3_600_000L, -1f))
    }
}
