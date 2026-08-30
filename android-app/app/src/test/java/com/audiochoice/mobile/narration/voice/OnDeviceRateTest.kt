package com.audiochoice.mobile.narration.voice

import com.audiochoice.mobile.data.VoiceKind
import com.audiochoice.mobile.narration.NarrationStorage
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class OnDeviceRateTest {

    // region the benchmark passage

    /**
     * A rate is only comparable across devices if they read the same words, so the passage is
     * fixed in the source rather than generated. Long enough that start-up cost does not dominate
     * the measurement, short enough that a listener will actually wait for it.
     */
    @Test
    fun `the benchmark passage is long enough to measure and short enough to wait for`() {
        assertTrue(
            "the passage is ${OnDeviceRate.BENCHMARK_CHARACTERS} characters, too short to " +
                "measure past engine start-up",
            OnDeviceRate.BENCHMARK_CHARACTERS in 300..1_500,
        )
        // Mixed narration and dialogue: punctuation-heavy text synthesizes differently from flat
        // prose, and a book is both.
        assertTrue(
            "the passage has no dialogue, so it does not represent a novel",
            OnDeviceRate.BENCHMARK_PASSAGE.any { it.contains('"') },
        )
        assertTrue(OnDeviceRate.BENCHMARK_PASSAGE.size >= 4)
    }

    // endregion

    // region the availability floor

    /**
     * Three times real time, not two.
     *
     * A device that only just keeps up while idle falls behind the moment the listener is also
     * scrolling, the screen is on, and something else wants the CPU -- which is the condition it
     * will actually run in.
     */
    @Test
    fun `an engine must beat real time by three to be offered`() {
        assertEquals(3.0, OnDeviceRate.MINIMUM_REAL_TIME_FACTOR, 0.0)
        assertFalse("a device at 2.9x was offered", OnDeviceRate.isFastEnough(2.9))
        assertTrue(OnDeviceRate.isFastEnough(3.0))
        assertTrue(OnDeviceRate.isFastEnough(12.0))
    }

    /** Below real time the engine can never catch up, whatever window it is given. */
    @Test
    fun `an engine slower than real time is never offered`() {
        listOf(0.0, 0.3, 0.9).forEach { factor ->
            assertFalse("$factor was treated as usable", OnDeviceRate.isFastEnough(factor))
        }
    }

    // endregion

    // region the render-ahead window

    /**
     * A faster engine needs a smaller lead because it recovers quickly; a slower one needs more.
     * Derived from a measurement rather than assumed, which is the point -- the default of one
     * chapter was chosen as a floor precisely because nothing had been measured.
     */
    @Test
    fun `the window shrinks as the engine gets faster`() {
        val windows = listOf(1.5, 3.0, 6.0, 20.0).map(OnDeviceRate::renderAheadChapters)
        assertEquals(
            "the window should never grow with speed: $windows",
            windows.sortedDescending(),
            windows,
        )
        assertEquals(1, OnDeviceRate.renderAheadChapters(20.0))
    }

    @Test
    fun `the window stays within its bounds for any measurement`() {
        listOf(-1.0, 0.0, 0.1, 0.99, 1.0, 2.5, 7.9, 8.0, 100.0).forEach { factor ->
            val window = OnDeviceRate.renderAheadChapters(factor)
            assertTrue(
                "$factor produced a window of $window",
                window in OnDeviceRate.DEFAULT_RENDER_AHEAD..OnDeviceRate.MAXIMUM_RENDER_AHEAD,
            )
        }
    }

    /** An unmeasured device falls back to the floor rather than to an invented window. */
    @Test
    fun `no measurement falls back to the default window`() {
        assertEquals(
            OnDeviceRate.DEFAULT_RENDER_AHEAD,
            OnDeviceRate.renderAheadChapters(0.0),
        )
    }

    // endregion

    // region derived values

    @Test
    fun `the rate arithmetic holds`() {
        val rate = MeasuredSynthesisRate(
            kind = VoiceKind.SYSTEM,
            charactersSynthesized = 600,
            elapsedMillis = 5_000,
            audioDurationMillis = 40_000,
        )
        assertEquals(8.0, rate.realTimeFactor, 0.001)
        assertEquals(120.0, rate.charactersPerSecondOfWork, 0.001)
        assertEquals(15.0, rate.charactersPerSecondOfAudio, 0.001)
    }

    /** A zero-length measurement must not divide by zero and must not look fast. */
    @Test
    fun `an empty measurement reports nothing rather than dividing by zero`() {
        val rate = MeasuredSynthesisRate(VoiceKind.SYSTEM, 0, 0, 0)
        assertEquals(0.0, rate.realTimeFactor, 0.0)
        assertEquals(0.0, rate.charactersPerSecondOfWork, 0.0)
        assertEquals(0.0, rate.charactersPerSecondOfAudio, 0.0)
        assertFalse(OnDeviceRate.isFastEnough(rate.realTimeFactor))
    }

    // endregion

    // region the real device measurement

    /**
     * The one measurement taken on real hardware, and what it does and does not settle.
     *
     * A current flagship at 28x needs the smallest window there is, so effort spent shrinking the
     * render-ahead window on fast phones would buy nothing. It says nothing about a four-year-old
     * mid-range phone, which is the device the availability gate exists for -- and that is still
     * unmeasured.
     */
    @Test
    fun `the measured flagship needs the smallest render-ahead window`() {
        val measured = OnDeviceRate.MEASURED_FLAGSHIP_REAL_TIME_FACTOR
        assertTrue(
            "a flagship at ${measured}x should clear the availability floor comfortably",
            OnDeviceRate.isFastEnough(measured),
        )
        assertEquals(
            OnDeviceRate.DEFAULT_RENDER_AHEAD,
            OnDeviceRate.renderAheadChapters(measured),
        )
        // And it must clear the floor by a wide margin, or the floor is set wrong.
        assertTrue(
            "the measured device only just clears the floor, which suggests the floor is too high",
            measured > OnDeviceRate.MINIMUM_REAL_TIME_FACTOR * 3,
        )
    }

    /**
     * The benchmark passage is exactly the length the real measurement was taken over. Changing it
     * would make every future device incomparable with the recorded figure without anybody
     * noticing, so its length is pinned.
     */
    @Test
    fun `the benchmark passage is the length the recorded measurement used`() {
        assertEquals(
            "the passage changed, so new measurements are not comparable with the recorded one",
            515,
            OnDeviceRate.BENCHMARK_CHARACTERS,
        )
    }

    // endregion

    // region the report a listener reads out

    /**
     * The reporting format carries more weight than it looks like it does. The build machine
     * cannot reach a phone, so the only route a measurement has from a device to the people who
     * need it is somebody reading it off the screen -- and anything ambiguous gets transcribed
     * wrong.
     */
    @Test
    fun `the report carries every number needed to act on it`() {
        val report = OnDeviceRate.report(
            MeasuredSynthesisRate(VoiceKind.SYSTEM, 600, 5_000, 40_000),
        )
        assertTrue(report, report.contains("SYSTEM"))
        assertTrue(report, report.contains("8.0x real time"))
        assertTrue(report, report.contains("15.0 chars/sec spoken"))
        assertTrue(report, report.contains("600 chars in 5000ms"))
        // And the conclusion, so nobody has to apply the threshold by hand.
        assertTrue(report, report.contains("OFFER"))
        assertTrue(report, report.contains("window 1"))
    }

    @Test
    fun `a slow device says so in words`() {
        val report = OnDeviceRate.report(
            MeasuredSynthesisRate(VoiceKind.LOCAL_NEURAL, 600, 40_000, 40_000),
        )
        assertTrue(report, report.contains("TOO SLOW"))
        assertFalse(report, report.contains("OFFER"))
    }

    /**
     * The measured speaking rate is what the storage estimate uses, so a device measurement can
     * be checked directly against the constant it would correct. Guarding the units here because
     * confusing "characters per second of work" with "characters per second of audio" is the easy
     * mistake, and it differs by the real-time factor -- which is to say, by a lot.
     */
    @Test
    fun `the spoken rate is comparable with the storage constant`() {
        val rate = MeasuredSynthesisRate(VoiceKind.SYSTEM, 1_000, 2_000, 60_000)
        // 1,000 characters over 60 seconds of audio is 16.7 characters a second spoken.
        assertEquals(16.67, rate.charactersPerSecondOfAudio, 0.01)
        // Which is the same quantity NarrationStorage estimates from, and in the same units.
        assertTrue(
            "the storage constant and the measured spoken rate are not comparable",
            NarrationStorage.charactersPerSecond(VoiceKind.SYSTEM) in 10.0..25.0,
        )
        // And emphatically not the work rate, which for the same measurement is 30x larger.
        assertEquals(500.0, rate.charactersPerSecondOfWork, 0.01)
    }

    // endregion
}
