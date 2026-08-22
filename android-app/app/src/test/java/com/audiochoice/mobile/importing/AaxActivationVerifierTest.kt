package com.audiochoice.mobile.importing

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class AaxActivationVerifierTest {
    @Test
    fun calculatesChecksumUsingDocumentedAaxChain() {
        assertArrayEquals(
            AaxActivationVerifier.hex("7b19e237cd6eef8770b30a93fe165070ab199e54"),
            AaxActivationVerifier.calculatedChecksum(0x1CEB00DAu),
        )
    }

    @Test
    fun verifiesOnlyMatchingActivationValue() {
        val checksum = AaxActivationVerifier.hex("7b19e237cd6eef8770b30a93fe165070ab199e54")
        assertTrue(AaxActivationVerifier.matches(0x1CEB00DAu, checksum))
        assertFalse(AaxActivationVerifier.matches(0x1CEB00DBu, checksum))
    }
}
