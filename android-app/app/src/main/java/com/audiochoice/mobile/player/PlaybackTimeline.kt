package com.audiochoice.mobile.player

import com.audiochoice.mobile.narration.NarrationTimeline

/**
 * Translates between what the media controller reports and the position the rest of
 * the player works in.
 *
 * For an imported audiobook these are the same number, because the playlist holds one
 * item. For a narrated book the controller reports a position within one chapter's
 * file, while progress, bookmarks, the sleep timer, completion, the chapter controls
 * and the reader all want a position in the book.
 *
 * One indirection rather than a translation at each reader. `PlayerViewModel` reads
 * position or duration in fourteen places, and converting at each of them would
 * guarantee that one is eventually missed -- most likely the progress checkpoint, where
 * the mistake is silent and permanent because it writes a wrong resume position to the
 * account.
 *
 * Deliberately expressed over primitives rather than over a `MediaController`, so the
 * arithmetic can be tested without Android. That matters most for
 * [DirectPlaybackTimeline], whose whole job is to be indistinguishable from reading the
 * controller directly.
 */
/**
 * What the player needs to know about a narrated book while it plays.
 *
 * Its presence is the marker the two playback guards test on, which is why it is null
 * for every imported audiobook: both guards are then inert on the path that ships today.
 */
data class NarrationPlaybackState(
    val renderedChapters: Int,
    val totalChapters: Int,
    val failedChapters: Int = 0,
) {
    /**
     * Whether the whole book exists yet.
     *
     * The completion check depends on this: a book whose duration is still growing has
     * not reached its end just because playback reached the end of the audio.
     */
    val fullyRendered: Boolean get() = totalChapters > 0 && renderedChapters == totalChapters

    val hasChaptersRemaining: Boolean get() = renderedChapters < totalChapters
}

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

/**
 * The narrated-book case: accumulate across the chapters that have been rendered.
 *
 * Duration is the total of rendered chapters only, so it grows as chapters arrive. That
 * is why the completion check needs its own guard: a book three chapters into forty
 * would otherwise reach "the end" of a duration that is still growing.
 */
class NarrationPlaybackTimeline(private val timeline: NarrationTimeline) : PlaybackTimeline {

    override fun bookPositionMs(itemIndex: Int, positionInItemMs: Long): Long =
        timeline.bookTimeMs(itemIndex, positionInItemMs)

    /**
     * The controller's per-item duration is discarded on purpose: it describes one
     * chapter's file, and reporting it as the book's length would make a forty-hour
     * novel look like a twenty-minute one.
     */
    override fun bookDurationMs(itemDurationMs: Long): Long = timeline.totalDurationMs

    override fun seekTarget(bookTimeMs: Long): SeekTarget {
        val (itemIndex, offset) = timeline.locate(bookTimeMs)
        return SeekTarget(itemIndex, offset)
    }
}
