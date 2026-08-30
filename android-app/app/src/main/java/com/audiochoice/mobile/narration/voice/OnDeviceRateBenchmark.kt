package com.audiochoice.mobile.narration.voice

import com.audiochoice.mobile.data.VoiceKind

/** One engine's measured synthesis rate on this device. */
data class MeasuredSynthesisRate(
    val kind: VoiceKind,
    val charactersSynthesized: Int,
    val elapsedMillis: Long,
    val audioDurationMillis: Long,
) {
    /**
     * How much faster than real time this engine synthesizes.
     *
     * The number the render-ahead window depends on. Below 1.0 the engine cannot keep up with a
     * listener at all: the audio takes longer to produce than to play, so the playhead catches
     * the renderer and stays caught.
     */
    val realTimeFactor: Double
        get() = if (elapsedMillis <= 0) 0.0 else audioDurationMillis / elapsedMillis.toDouble()

    /** Characters of text turned into audio per second of wall-clock time. */
    val charactersPerSecondOfWork: Double
        get() = if (elapsedMillis <= 0) 0.0 else charactersSynthesized * 1_000.0 / elapsedMillis

    /** Characters per second of *audio*, which is the speaking rate. */
    val charactersPerSecondOfAudio: Double
        get() = if (audioDurationMillis <= 0) 0.0 else charactersSynthesized * 1_000.0 / audioDurationMillis
}

/**
 * Turns a measured rate into the decisions that depend on it.
 *
 * Pure, so the thresholds can be reasoned about without a device, and so the device only has to
 * report numbers rather than conclusions.
 */
object OnDeviceRate {

    /**
     * The passage every device is measured on.
     *
     * Fixed, and fixed in the source rather than generated, because a rate is only comparable
     * across devices if they read the same words. Mixed narration and dialogue, because
     * punctuation-heavy text synthesizes differently from flat prose and a book is both.
     */
    val BENCHMARK_PASSAGE: List<String> = listOf(
        "She had not expected him to be waiting.",
        "The rain had stopped an hour ago, and the street still held that washed, expectant " +
            "quiet that comes after a long storm has finally moved on towards the sea.",
        "\"You came,\" he said.",
        "Something in his voice made her stop three steps short of him, close enough to see " +
            "the water still beading on his collar.",
        "\"Did you think I wouldn't?\"",
        "She had rehearsed a dozen versions of this conversation on the way over, and every " +
            "single one of them had deserted her the moment she turned the corner.",
    )

    val BENCHMARK_CHARACTERS: Int get() = BENCHMARK_PASSAGE.sumOf { it.length }

    /**
     * The floor below which an engine is not offered.
     *
     * Three times real time. Not two: a device that only just keeps up while idle will fall
     * behind the moment the listener is also scrolling, the screen is on, and something else
     * wants the CPU. Three leaves room for a book to stay ahead of a playhead on a device that
     * is doing other things, which is the condition it will actually run in.
     */
    const val MINIMUM_REAL_TIME_FACTOR = 3.0

    /** Whether an engine measured at [factor] should be offered to a listener at all. */
    fun isFastEnough(factor: Double): Boolean = factor >= MINIMUM_REAL_TIME_FACTOR

    /**
     * How many chapters to keep rendered ahead of the playhead, from a measured rate.
     *
     * The window exists so a listener never reaches the end of rendered audio while more is
     * still being made. A faster engine needs a smaller lead, because it recovers quickly; a
     * slower one needs more, because it does not.
     *
     * Derived rather than assumed, which is the whole point: the default of one chapter was
     * chosen as a floor precisely because nothing had been measured.
     */
    fun renderAheadChapters(factor: Double): Int = when {
        factor <= 0.0 -> DEFAULT_RENDER_AHEAD
        // Below real time no window saves the listener; the engine should not be offered.
        factor < 1.0 -> MAXIMUM_RENDER_AHEAD
        factor < 2.0 -> 4
        factor < 4.0 -> 3
        factor < 8.0 -> 2
        else -> 1
    }

    const val DEFAULT_RENDER_AHEAD = 1
    const val MAXIMUM_RENDER_AHEAD = 5

    /**
     * The one real device measurement so far.
     *
     * Samsung SM-S936U, Android 16, 2026-08-29: **28.2x real time**, 18.4 characters per second
     * spoken, 515 characters synthesized in 992 ms. A current flagship, and comfortably past the
     * 3x floor -- it needs a lead of one chapter, which is the smallest window there is.
     *
     * Recorded here because of what it does *not* establish. A flagship at 28x says nothing about
     * a four-year-old mid-range phone, which is the device the availability gate exists for. The
     * useful conclusion is narrower than it looks: on a fast phone the on-device voice is not the
     * bottleneck, so effort spent shrinking the render-ahead window would buy nothing. Whether a
     * slow phone clears 3x at all is still unmeasured, and the gate is what handles it.
     */
    const val MEASURED_FLAGSHIP_REAL_TIME_FACTOR = 28.2

    /**
     * A single line a listener can read off the screen and send back.
     *
     * The reporting format matters more than it looks: this Mac cannot reach a phone, so the
     * only route a measurement has from a device to the people who need it is somebody reading
     * it out. Anything ambiguous gets transcribed wrong.
     */
    fun report(rate: MeasuredSynthesisRate): String = buildString {
        append(rate.kind.name)
        append(": ")
        append("%.1fx real time".format(rate.realTimeFactor))
        append(", ")
        append("%.1f chars/sec spoken".format(rate.charactersPerSecondOfAudio))
        append(", ")
        append("${rate.charactersSynthesized} chars in ${rate.elapsedMillis}ms")
        append(" -> ")
        append(
            if (isFastEnough(rate.realTimeFactor)) {
                "OFFER, window ${renderAheadChapters(rate.realTimeFactor)}"
            } else {
                "TOO SLOW, do not offer"
            },
        )
    }
}
