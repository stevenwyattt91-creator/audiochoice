package com.audiochoice.mobile.player

/**
 * How long a book will really take, as distinct from how long it is.
 */
object ListeningTime {

    /**
     * Wall-clock milliseconds left in a book with [remainingBookMs] of audio still to play at
     * [speed].
     *
     * The player's remaining figure used to be the book's own remaining length, which at 1.5x
     * counted down half again as fast as the clock: a listener could see the number falling
     * quicker, but it never told them when they would actually finish. Dividing by the rate
     * makes it answer the question being asked of it, and changes the moment the speed does.
     *
     * A speed of zero or less cannot be listened at, so it reads as normal speed rather than
     * being divided by. Neither platform can set one -- both clamp -- but the value outlives
     * the build that wrote it, and an infinite time remaining would be a poor way to discover
     * a stored speed had been corrupted.
     */
    fun remainingRealMs(remainingBookMs: Long, speed: Float): Long {
        if (remainingBookMs <= 0L) return 0L
        val rate = if (speed > 0f) speed.toDouble() else 1.0
        return (remainingBookMs / rate).toLong()
    }
}
