package com.audiochoice.mobile.player

/**
 * When an audiobook counts as finished.
 *
 * Reaching the exact final sample almost never happens: books end with credits, an outro or
 * a few seconds of silence, and listeners stop before that. Requiring the very end would
 * mean nothing was ever marked complete.
 *
 * Mirrors BookCompletion on iOS. The same account sees the same books on both, and a book
 * that reads as finished on one and unfinished on the other is worse than either answer.
 */
object BookCompletion {
    /**
     * How close to the end still counts as finished, for a book long enough that this is a
     * small fraction of the whole.
     */
    const val REMAINING_MS: Long = 30_000

    /**
     * The fallback for short files, where thirty seconds could be most of the runtime, or
     * even a negative threshold that marked them finished the moment they opened.
     */
    const val COMPLETED_FRACTION: Double = 0.98

    fun isComplete(positionMs: Long, durationMs: Long): Boolean {
        // Duration is 0 until the media item is prepared. Treating that as complete would
        // mark a book finished simply for having been opened.
        if (durationMs <= 0L || positionMs <= 0L) return false
        return positionMs >= thresholdMs(durationMs)
    }

    fun thresholdMs(durationMs: Long): Long {
        if (durationMs <= 0L) return Long.MAX_VALUE
        val byRemaining = durationMs - REMAINING_MS
        val byFraction = (durationMs * COMPLETED_FRACTION).toLong()
        return maxOf(byRemaining, byFraction)
    }
}
