package com.audiochoice.mobile.beta

import org.junit.Assert.assertEquals
import org.junit.Test

class BetaConfigTest {

    /**
     * Owner test access compares a SHA-256 digest, so the hex encoding has to be
     * exactly right. Kotlin's Byte is signed, and a naive encoding of a byte
     * above 0x7f is a classic source of silently wrong digests -- which here
     * would mean owner access stops working with no visible error.
     *
     * These are the published SHA-256 test vectors.
     */
    @Test
    fun `sha256Hex matches published test vectors`() {
        assertEquals(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            BetaConfig.sha256Hex("abc"),
        )
        assertEquals(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            BetaConfig.sha256Hex(""),
        )
    }

    /** Digests are lowercase hex and always the full 64 characters. */
    @Test
    fun `sha256Hex is zero padded lowercase hex`() {
        val digest = BetaConfig.sha256Hex("audiochoice")
        assertEquals(64, digest.length)
        assertEquals(digest.lowercase(), digest)
        assertEquals(true, digest.all { it in '0'..'9' || it in 'a'..'f' })
    }
}
