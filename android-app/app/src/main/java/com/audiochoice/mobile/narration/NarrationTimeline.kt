package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.ReaderTimingRange

/**
 * The one place per-chapter audio becomes one continuous position space.
 *
 * A narrated book is a set of files that appear over time, not one file that exists
 * up front, and the media session addresses them as playlist items with per-item
 * positions. Everything above the player -- progress, bookmarks, the sleep timer,
 * completion, the reader -- wants a single number line instead. Converting in one
 * place is what lets all of those keep working without knowing which kind of book
 * they are looking at.
 *
 * Stored timelines are chapter-relative, measured from the first sample of their own
 * chapter's audio. The cumulative offset is applied here, at load. That is what makes
 * re-rendering one chapter cheap: a chapter that comes back a different length
 * invalidates no other chapter's stored timings, because none of them ever recorded
 * where they sat in the book.
 */
class NarrationTimeline(val chapters: List<RenderedChapter>) {

    /**
     * One rendered chapter's place in the book.
     *
     * [planIndex] is kept because the playlist only contains rendered chapters, so
     * item index and chapter index are not the same number once anything is missing.
     * Conflating them is the obvious bug here.
     */
    data class RenderedChapter(
        val planIndex: Int,
        val bookStartMs: Long,
        val durationMs: Long,
        /** Chapter-relative, exactly as persisted. */
        val timings: List<ReaderTimingRange>,
    ) {
        val bookEndMs: Long get() = bookStartMs + durationMs
    }

    /** Rendered chapters only, so duration grows as rendering proceeds. */
    val totalDurationMs: Long = chapters.sumOf { it.durationMs }

    val isEmpty: Boolean get() = chapters.isEmpty()

    /**
     * Book position from a playlist item index and a position within that item.
     *
     * An unknown item reports zero rather than throwing: a controller can briefly
     * report an index from a playlist that has just been replaced, and a crash there
     * would be a crash during ordinary re-rendering.
     */
    fun bookTimeMs(itemIndex: Int, positionInItemMs: Long): Long {
        val chapter = chapters.getOrNull(itemIndex) ?: return 0L
        return chapter.bookStartMs + positionInItemMs.coerceIn(0L, chapter.durationMs)
    }

    /**
     * The playlist item and in-item offset for a book position.
     *
     * Clamped rather than nullable. Every caller is about to perform a seek, and the
     * useful behaviour at the ends is to land at the start or at the end of what
     * exists rather than to do nothing.
     */
    fun locate(bookTimeMs: Long): Pair<Int, Long> {
        if (chapters.isEmpty()) return 0 to 0L
        val clamped = bookTimeMs.coerceIn(0L, totalDurationMs)

        var low = 0
        var high = chapters.lastIndex
        while (low < high) {
            val middle = (low + high + 1) / 2
            if (chapters[middle].bookStartMs <= clamped) low = middle else high = middle - 1
        }
        val chapter = chapters[low]
        return low to (clamped - chapter.bookStartMs).coerceIn(0L, chapter.durationMs)
    }

    /** The plan index of the chapter containing a book position. */
    fun planIndexAt(bookTimeMs: Long): Int? =
        chapters.getOrNull(locate(bookTimeMs).first)?.planIndex

    /**
     * Every chapter's timings offset into book time and concatenated.
     *
     * Ordered by both start time and start character, because chapters are in plan
     * order, plan order is spine order, and units ascend within a chapter. Both
     * reader conversions rely on that ordering -- one binary searches on time, the
     * other scans on characters -- so the reader needs no change to work here.
     */
    val narrationTimingRanges: List<ReaderTimingRange> by lazy {
        chapters.flatMap { chapter ->
            val offsetSeconds = chapter.bookStartMs / 1_000.0
            chapter.timings.map { timing ->
                timing.copy(
                    startTime = timing.startTime + offsetSeconds,
                    endTime = timing.endTime + offsetSeconds,
                )
            }
        }
    }

    companion object {
        val EMPTY = NarrationTimeline(emptyList())

        /**
         * Build a timeline from per-chapter durations and timings.
         *
         * [renderedPlanIndices] is the subset of chapters that have audio, in plan
         * order. Only those advance the clock, which is why an unrendered chapter in
         * the middle of a book does not leave a silent hole in the position space.
         */
        fun of(
            renderedPlanIndices: List<Int>,
            durationsMs: (Int) -> Long,
            timings: (Int) -> List<ReaderTimingRange>,
        ): NarrationTimeline {
            var cumulative = 0L
            val chapters = renderedPlanIndices.map { planIndex ->
                val duration = durationsMs(planIndex).coerceAtLeast(0L)
                val chapter = RenderedChapter(
                    planIndex = planIndex,
                    bookStartMs = cumulative,
                    durationMs = duration,
                    timings = timings(planIndex),
                )
                cumulative += duration
                chapter
            }
            return NarrationTimeline(chapters)
        }
    }
}
