package com.audiochoice.mobile.player

/**
 * Translates between what the media controller reports and the position the rest of
 * the player works in.
 *
 * For an imported audiobook these are the same number, because the playlist holds one
 * item. The indirection exists so `PlayerViewModel` reads position or duration through
 * one place rather than at each of the fourteen call sites that want it, which would
 * guarantee that one is eventually missed -- most likely the progress checkpoint, where
 * the mistake is silent and permanent because it writes a wrong resume position to the
 * account.
 *
 * Deliberately expressed over primitives rather than over a `MediaController`, so the
 * arithmetic can be tested without Android. That matters most for
 * [DirectPlaybackTimeline], whose whole job is to be indistinguishable from reading the
 * controller directly.
 */
interface PlaybackTimeline {

    /** Book position, from the controller's item index and position within that item. */
    fun bookPositionMs(itemIndex: Int, positionInItemMs: Long): Long

    /**
     * Book duration, from what the controller reports for the current item.
     *
     * The value passed in may be `C.TIME_UNSET` while a duration is still unknown, and
     * an implementation that reports the controller's own number must pass that through
     * unchanged: callers already treat a non-positive duration as "not known yet".
     */
    fun bookDurationMs(itemDurationMs: Long): Long

    /** Where to seek for a book position. */
    fun seekTarget(bookTimeMs: Long): SeekTarget
}

/**
 * A seek instruction.
 *
 * A null [itemIndex] means "seek within whatever is playing", which is the single-item
 * case and is exactly the call the player made before this indirection existed.
 */
data class SeekTarget(val itemIndex: Int?, val positionMs: Long)

/**
 * The imported-audiobook case: report the controller's own numbers, unchanged.
 *
 * This exists to be boring. Every assertion about it is an identity, and its tests are
 * regression tests for the books people are already listening to rather than tests of
 * new behaviour.
 */
object DirectPlaybackTimeline : PlaybackTimeline {

    override fun bookPositionMs(itemIndex: Int, positionInItemMs: Long): Long = positionInItemMs

    override fun bookDurationMs(itemDurationMs: Long): Long = itemDurationMs

    override fun seekTarget(bookTimeMs: Long): SeekTarget = SeekTarget(null, bookTimeMs)
}
