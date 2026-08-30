package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.RenderState

/**
 * Decides what to render next, and nothing else.
 *
 * The whole render policy is one pure function on purpose. Rendering involves a
 * background worker, a text-to-speech engine, a network call and a media session,
 * and none of those are things you want in the loop when reasoning about "should
 * this chapter be produced now". Keeping the decision separate means the policy is
 * a table test rather than an integration test, and it means the same inputs always
 * choose the same chapter.
 *
 * The window exists for two unrelated reasons that happen to want the same answer.
 * Producing chapters a listener never reaches wastes premium synthesis on a book
 * abandoned at chapter three, and it wastes battery and hours of CPU on a device
 * rendering a novel it was never asked to finish. Staying a few chapters ahead of
 * the playhead solves both.
 */
object NarrationRenderScheduler {

    /**
     * The index to render, or null when nothing should be rendered right now.
     *
     * [renderAheadWindow] is supplied by the caller from configuration rather than
     * being a constant here, because its value is derived from measured synthesis
     * throughput that does not exist yet. One is the floor: rendering nothing ahead
     * would stall the moment a chapter ended.
     */
    fun nextChapterToRender(
        states: List<RenderState>,
        playheadChapter: Int,
        renderAheadWindow: Int,
        fullBookRequested: Boolean = false,
        pausedByListener: Boolean = false,
    ): Int? {
        if (states.isEmpty()) return null

        // A pause is a pause. Not "finish the current chapter first", because the
        // listener who paused rendering is usually the listener watching their
        // battery drain.
        if (pausedByListener) return null

        if (fullBookRequested) {
            return states.indexOfFirst { it == RenderState.NOT_RENDERED }.takeIf { it >= 0 }
        }

        val playhead = playheadChapter.coerceIn(0, states.lastIndex)
        if (readyAhead(states, playhead) >= renderAheadWindow.coerceAtLeast(1)) return null

        // From the playhead forward. A chapter before the playhead is behind the
        // listener, and re-rendering it would spend the window on audio nobody is
        // walking toward.
        return (playhead..states.lastIndex)
            .firstOrNull { states[it] == RenderState.NOT_RENDERED }
    }

    /**
     * The *contiguous* run of rendered chapters after [playheadChapter].
     *
     * Contiguous, not the total rendered somewhere ahead. A gap is a wall the
     * listener will hit, so counting past it would report the window satisfied while
     * playback stalls a few minutes later. This is the one line in the scheduler most
     * likely to be "simplified" into a `count` by someone who has not hit that stall.
     */
    fun readyAhead(states: List<RenderState>, playheadChapter: Int): Int {
        var count = 0
        var index = playheadChapter + 1
        while (index < states.size && states[index] == RenderState.RENDERED) {
            count++
            index++
        }
        return count
    }

    /**
     * Whether anything is left to do, ignoring the window.
     *
     * Used to decide whether the worker should keep itself alive, which is a
     * different question from which chapter is next: the window can be satisfied
     * while chapters remain.
     */
    fun hasWorkRemaining(states: List<RenderState>): Boolean =
        states.any { it == RenderState.NOT_RENDERED }

    /**
     * Whether every chapter has been attempted and at least one failed.
     *
     * A book in this state cannot be finished with the current voice, so the listener
     * is offered a retry or a different voice rather than watching a queue that will
     * never move.
     */
    fun isStalledByFailures(states: List<RenderState>): Boolean =
        states.isNotEmpty() &&
            states.none { it == RenderState.NOT_RENDERED || it == RenderState.RENDERING } &&
            states.any { it == RenderState.RENDER_FAILED }
}

/**
 * The render-ahead window in effect.
 *
 * Deliberately not a constant. The value follows from how fast the chosen voice
 * actually synthesises relative to the fastest speed a listener can play, and that
 * throughput has not been measured on either the premium endpoint or a mid-range
 * device. Fixing a number here would mean either a listener waiting mid-chapter or a
 * bill for chapters nobody reached.
 *
 * [DEFAULT] is the floor rather than an estimate: one chapter ahead is the least that
 * can work at all, so it is safe to ship while the measurement is outstanding.
 */
data class RenderAheadWindow(val chapters: Int) {

    init {
        require(chapters >= MINIMUM) { "The render-ahead window must be at least $MINIMUM" }
    }

    companion object {
        const val MINIMUM = 1

        /** Used until a measured synthesis rate replaces it. */
        val DEFAULT = RenderAheadWindow(MINIMUM)

        /**
         * Derive the window from a measured synthesis rate.
         *
         * The window has to cover producing one chapter at [synthesisRate] while the
         * listener consumes audio at [playbackSpeedCeiling]. A rate below the ceiling
         * cannot keep up at all, so it gets a wider window and the caller is expected
         * to have refused to offer that voice.
         */
        fun fromMeasuredRate(synthesisRate: Double, playbackSpeedCeiling: Double): RenderAheadWindow {
            if (synthesisRate <= 0.0) return DEFAULT
            val needed = kotlin.math.ceil(playbackSpeedCeiling / synthesisRate).toInt() + 1
            return RenderAheadWindow(needed.coerceAtLeast(MINIMUM))
        }
    }
}
