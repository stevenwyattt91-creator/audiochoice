package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.AudioChapter
import com.audiochoice.mobile.data.NarrationPlan
import com.audiochoice.mobile.data.RenderQueue
import com.audiochoice.mobile.data.RenderState

/**
 * Presents narration chapters through the chapter type the player already uses.
 *
 * `AudioChapter` is `(title, startSeconds, endSeconds)` and has no representation
 * for a chapter that has not been produced yet, which every narrated book has
 * plenty of. Rather than widen the type or fork the chapter controls, an unrendered
 * chapter becomes a zero-length entry at the current end of rendered audio.
 *
 * That is not a trick for its own sake; it makes the three existing controls behave
 * correctly with no change to them:
 *
 * - `previousChapter` finds the current chapter with
 *   `indexOfLast { startSeconds <= position }`. While the playhead is inside
 *   rendered audio, a zero-length entry sitting at the end never matches, so the
 *   current chapter is a real one.
 * - `nextChapter` takes `firstOrNull { startSeconds > position + 1.0 }`, so an
 *   unrendered chapter is only reachable from inside the last rendered one, and
 *   seeking to it lands at the end of rendered audio. That is exactly what the
 *   requirement asks for when a listener seeks into a chapter that is not ready.
 * - `sleepAtEndOfChapter` needs `position >= start && position < end`, which a
 *   zero-length entry can never satisfy, so the sleep timer can never target a
 *   chapter that has no audio.
 *
 * The cost is that a partially rendered book's chapter list contains entries that
 * describe no real audio, so anything reading that list must treat a zero-length
 * chapter as "not yet rendered".
 */
object NarrationChapters {

    /**
     * Book-relative start of each chapter's audio, in milliseconds.
     *
     * Only rendered chapters advance the clock. An unrendered chapter reports the
     * running total, which is where a seek toward it should land.
     */
    fun chapterStartsMs(queue: RenderQueue): List<Long> {
        var cumulative = 0L
        return queue.states.indices.map { index ->
            val start = cumulative
            if (queue.states[index] == RenderState.RENDERED) {
                cumulative += queue.chapterDurationsMs.getOrElse(index) { 0L }
            }
            start
        }
    }

    fun audioChapters(plan: NarrationPlan, queue: RenderQueue): List<AudioChapter> {
        if (plan.chapters.isEmpty()) return emptyList()
        val starts = chapterStartsMs(queue)
        return plan.chapters.mapIndexed { index, chapter ->
            val startMs = starts.getOrElse(index) { 0L }
            val rendered = queue.states.getOrNull(index) == RenderState.RENDERED
            val durationMs = if (rendered) queue.chapterDurationsMs.getOrElse(index) { 0L } else 0L
            AudioChapter(
                title = chapter.title,
                startSeconds = startMs / 1000.0,
                endSeconds = (startMs + durationMs) / 1000.0,
            )
        }
    }

    /**
     * The chapter containing a book position, by the same rule the player's
     * `previousChapter` uses, so the two never disagree about where the playhead is.
     */
    fun chapterIndexAt(queue: RenderQueue, bookTimeMs: Long): Int {
        val starts = chapterStartsMs(queue)
        var result = 0
        starts.indices.forEach { index ->
            val rendered = queue.states.getOrNull(index) == RenderState.RENDERED
            val durationMs = if (rendered) queue.chapterDurationsMs.getOrElse(index) { 0L } else 0L
            // A zero-length entry is not a place the playhead can be inside, so it
            // is skipped rather than claimed.
            if (durationMs > 0 && starts[index] <= bookTimeMs) result = index
        }
        return result
    }
}
