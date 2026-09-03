package com.audiochoice.mobile.reader

import com.audiochoice.mobile.data.ReaderTimingRange

/**
 * Maps between audio time and reading position using the server's alignment.
 *
 * Coverage is deliberately sparse: `ReaderAlignment.Create` skips any transcript
 * segment it cannot confidently anchor in the EPUB, so the ranges are neither
 * contiguous nor complete. Both directions therefore return null rather than
 * guessing, and the caller keeps its previous state across a gap instead of
 * snapping to the wrong place.
 *
 * Ranges are ordered by both time and character, because the server walks the
 * transcript with a monotonic cursor.
 */

/** Character offset currently being narrated, or null if no range covers [seconds]. */
/**
 * Roughly where the listening has reached, for a book with no alignment.
 *
 * A straight proportion of the text: two hours into a ten-hour book lands a fifth of the way in.
 * Wrong by pages, and deliberately offered anyway. A book whose EPUB was never aligned -- and
 * plenty were not, because alignment is a separate request that can fail while the filter scan
 * succeeds -- otherwise opens at the title page ten hours in, with nothing to search for. Being
 * approximately right is the difference between a starting point and no way back at all.
 *
 * Null when the numbers cannot support even that: nothing played, no runtime, or no text.
 */
fun approximateReaderCharacter(seconds: Double, durationSeconds: Double, characterCount: Int): Int? {
    if (seconds <= 0 || durationSeconds <= 0 || characterCount <= 0) return null
    if (!seconds.isFinite() || !durationSeconds.isFinite()) return null
    val fraction = (seconds / durationSeconds).coerceIn(0.0, 1.0)
    return (fraction * characterCount).toInt()
}

fun readerCharacterForTime(timings: List<ReaderTimingRange>, seconds: Double): Int? {
    val timing = timingContainingTime(timings, seconds) ?: return null
    val duration = (timing.endTime - timing.startTime).coerceAtLeast(MINIMUM_DURATION)
    val fraction = ((seconds - timing.startTime) / duration).coerceIn(0.0, 1.0)
    val length = timing.endCharacter - timing.startCharacter
    return timing.startCharacter + (length * fraction).toInt()
}

/**
 * Audio time for a character offset, for tap-to-seek.
 *
 * Falls forward to the next aligned range when the tapped text has no timing of
 * its own, so tapping an unaligned paragraph still moves the audio somewhere
 * sensible rather than doing nothing.
 */
fun readerTimeForCharacter(timings: List<ReaderTimingRange>, character: Int): Double? {
    if (timings.isEmpty()) return null
    val containing = timings.firstOrNull {
        character >= it.startCharacter && character < it.endCharacter
    }
    if (containing != null) {
        val length = (containing.endCharacter - containing.startCharacter).coerceAtLeast(1)
        val fraction = ((character - containing.startCharacter).toDouble() / length).coerceIn(0.0, 1.0)
        val duration = containing.endTime - containing.startTime
        return containing.startTime + duration * fraction
    }
    return timings.firstOrNull { it.startCharacter >= character }?.startTime
}

private fun timingContainingTime(
    timings: List<ReaderTimingRange>,
    seconds: Double,
): ReaderTimingRange? {
    var low = 0
    var high = timings.size - 1
    while (low <= high) {
        val middle = (low + high) / 2
        val timing = timings[middle]
        when {
            seconds < timing.startTime -> high = middle - 1
            seconds >= timing.endTime -> low = middle + 1
            else -> return timing
        }
    }
    return null
}

private const val MINIMUM_DURATION = 0.001
