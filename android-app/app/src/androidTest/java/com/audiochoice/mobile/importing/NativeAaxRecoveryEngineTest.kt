package com.audiochoice.mobile.importing

import androidx.test.ext.junit.runners.AndroidJUnit4
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import kotlin.system.measureTimeMillis

@RunWith(AndroidJUnit4::class)
class NativeAaxRecoveryEngineTest {
    @Test
    fun findsKnownValueInSmallRange() = runBlocking {
        val engine = NativeAaxRecoveryEngine()
        assertTrue(engine.available)
        val checksum = AaxActivationVerifier.hex("7b19e237cd6eef8770b30a93fe165070ab199e54")
        assertEquals(0x1CEB00DAu, engine.searchRange(checksum, 0x1CEAF000u, 0x1CEB1000u, 4))
    }

    @Test
    fun checkpointsAndMeasuresOneMillionCandidates() = runBlocking {
        val engine = NativeAaxRecoveryEngine()
        val checksum = AaxActivationVerifier.hex("7b19e237cd6eef8770b30a93fe165070ab199e54")
        val checkpoints = mutableListOf<AaxRecoveryCheckpoint>()
        val elapsed = measureTimeMillis {
            assertEquals(
                null,
                engine.searchWithCheckpoints(
                    fileChecksum = checksum,
                    startInclusive = 0u,
                    endInclusive = 999_999u,
                    chunkSize = 250_000u,
                    threads = 4,
                    onCheckpoint = { checkpoints.add(it) },
                ),
            )
        }
        assertEquals(4, checkpoints.size)
        assertEquals(1f, checkpoints.last().progress, 0.0001f)
        assertTrue("One million candidates took ${elapsed}ms", elapsed > 0)
        println("AAX_BENCHMARK one_million_candidates_ms=$elapsed")
    }
}
