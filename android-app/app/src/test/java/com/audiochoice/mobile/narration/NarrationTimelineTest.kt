package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.ReaderTimingRange
import com.audiochoice.mobile.reader.readerCharacterForTime
import com.audiochoice.mobile.reader.readerTimeForCharacter
import io.kotest.property.Arb
import io.kotest.property.PropTestConfig
import io.kotest.property.arbitrary.int
import io.kotest.property.arbitrary.list
import io.kotest.property.checkAll
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The timeline is the only place per-chapter audio becomes one number line, so its
 * two directions have to invert each other. If they drift, tapping a paragraph seeks
 * somewhere else and the highlight follows the wrong sentence, and neither failure is
 * visible from reading the code.
 */
class NarrationTimelineTest {

    // region round-trip properties

    /**
     * A character offset converted to book time and back lands inside the unit it
     * started in.
     *
     * Not "returns the same offset": both conversions interpolate linearly inside a
     * range, so the exact character is not preserved and does not need to be. What
     * matters is that it never lands in a different unit, because that is what would
     * put the highlight on the wrong sentence.
     */
    @Test
    fun `character to time and back stays inside the original unit`(): Unit = runBlocking {
        checkAll(PropTestConfig(iterations = 200), Arb.list(Arb.int(1..6), 1..5)) { unitsPerChapter ->
            val timeline = timelineOf(unitsPerChapter)
            val ranges = timeline.narrationTimingRanges
            if (ranges.isEmpty()) return@checkAll

            ranges.forEach { range ->
                // Sample inside the range, including both ends.
                listOf(
                    range.startCharacter,
                    (range.startCharacter + range.endCharacter) / 2,
                    range.endCharacter - 1,
                ).forEach { character ->
                    val time = readerTimeForCharacter(ranges, character)
                    assertNotNull("no time for character $character", time)
                    val back = readerCharacterForTime(ranges, time!!)
                    assertNotNull("no character for time $time", back)
                    assertTrue(
                        "character $character became $back, outside " +
                            "[${range.startCharacter}, ${range.endCharacter})",
                        back!! >= range.startCharacter && back < range.endCharacter,
                    )
                }
            }
        }
    }

    /**
     * Ordered by both time and character. Both reader conversions rely on it -- one
     * binary searches on time, the other scans on characters -- which is why the
     * reader works on a narrated book without modification.
     */
    @Test
    fun `timing ranges are ordered by both time and character`(): Unit = runBlocking {
        checkAll(PropTestConfig(iterations = 200), Arb.list(Arb.int(1..6), 1..6)) { unitsPerChapter ->
            val ranges = timelineOf(unitsPerChapter).narrationTimingRanges

            ranges.zipWithNext().forEach { (previous, next) ->
                assertTrue(
                    "times went backwards: ${previous.endTime} then ${next.startTime}",
                    next.startTime >= previous.startTime,
                )
                assertTrue(
                    "characters went backwards: ${previous.endCharacter} then " +
                        "${next.startCharacter}",
                    next.startCharacter >= previous.startCharacter,
                )
            }
        }
    }

    /** `locate` inverts `bookTimeMs`, including exactly on every chapter boundary. */
    @Test
    fun `locate inverts book time at every chapter boundary`() {
        val timeline = NarrationTimeline.of(
            renderedPlanIndices = listOf(0, 1, 2),
            durationsMs = { index -> listOf(10_000L, 5_000L, 20_000L)[index] },
            timings = { emptyList() },
        )

        timeline.chapters.forEachIndexed { itemIndex, chapter ->
            val (locatedItem, offset) = timeline.locate(chapter.bookStartMs)
            assertEquals(itemIndex, locatedItem)
            assertEquals(0L, offset)

            val inside = chapter.bookStartMs + chapter.durationMs / 2
            val (insideItem, insideOffset) = timeline.locate(inside)
            assertEquals(itemIndex, insideItem)
            assertEquals(inside, timeline.bookTimeMs(insideItem, insideOffset))
        }
    }

    // endregion

    // region cumulative arithmetic

    @Test
    fun `duration covers rendered chapters only and grows as they arrive`() {
        val one = NarrationTimeline.of(listOf(0), { 10_000L }, { emptyList() })
        val two = NarrationTimeline.of(listOf(0, 1), { 10_000L }, { emptyList() })

        assertEquals(10_000L, one.totalDurationMs)
        assertEquals(20_000L, two.totalDurationMs)
    }

    /**
     * An unrendered chapter in the middle leaves no silent hole. Only rendered
     * chapters advance the clock, so the position space is continuous over whatever
     * audio exists.
     */
    @Test
    fun `a gap in the middle leaves no hole in the position space`() {
        // Chapter 1 is missing; 0 and 2 are rendered.
        val timeline = NarrationTimeline.of(
            renderedPlanIndices = listOf(0, 2),
            durationsMs = { 10_000L },
            timings = { emptyList() },
        )

        assertEquals(20_000L, timeline.totalDurationMs)
        assertEquals(0L, timeline.chapters[0].bookStartMs)
        assertEquals(10_000L, timeline.chapters[1].bookStartMs)
        // Plan index and item index are different numbers once something is missing.
        assertEquals(2, timeline.chapters[1].planIndex)
        assertEquals(2, timeline.planIndexAt(15_000L))
    }

    @Test
    fun `chapter relative timings are offset into book time`() {
        val timeline = NarrationTimeline.of(
            renderedPlanIndices = listOf(0, 1),
            durationsMs = { 10_000L },
            timings = { index ->
                // Both chapters store timings starting at zero, as persisted.
                listOf(ReaderTimingRange(0.0, 4.0, index * 100, index * 100 + 40))
            },
        )

        val ranges = timeline.narrationTimingRanges
        assertEquals(0.0, ranges[0].startTime, 0.0001)
        // Second chapter's timings shifted by the first chapter's ten seconds.
        assertEquals(10.0, ranges[1].startTime, 0.0001)
        assertEquals(14.0, ranges[1].endTime, 0.0001)
        // Character offsets are untouched: they already index Book_Text.
        assertEquals(100, ranges[1].startCharacter)
    }

    // endregion

    // region edges

    @Test
    fun `an empty timeline reports nothing rather than throwing`() {
        val timeline = NarrationTimeline.EMPTY

        assertTrue(timeline.isEmpty)
        assertEquals(0L, timeline.totalDurationMs)
        assertEquals(0 to 0L, timeline.locate(5_000L))
        assertEquals(0L, timeline.bookTimeMs(0, 1_000L))
        assertNull(timeline.planIndexAt(0L))
        assertTrue(timeline.narrationTimingRanges.isEmpty())
    }

    /**
     * A controller can briefly report an item index from a playlist that has just
     * been replaced, which happens during ordinary re-rendering. Reporting zero there
     * is better than crashing.
     */
    @Test
    fun `an unknown item index reports zero rather than throwing`() {
        val timeline = NarrationTimeline.of(listOf(0), { 10_000L }, { emptyList() })

        assertEquals(0L, timeline.bookTimeMs(itemIndex = 7, positionInItemMs = 500L))
    }

    @Test
    fun `positions are clamped to what exists`() {
        val timeline = NarrationTimeline.of(listOf(0, 1), { 10_000L }, { emptyList() })

        assertEquals(0 to 0L, timeline.locate(-5_000L))
        assertEquals(1 to 10_000L, timeline.locate(999_999L))
        // A position beyond a chapter's own length is clamped to that chapter.
        assertEquals(10_000L, timeline.bookTimeMs(0, 50_000L))
    }

    @Test
    fun `a zero length chapter does not swallow the position space`() {
        val timeline = NarrationTimeline.of(
            renderedPlanIndices = listOf(0, 1, 2),
            durationsMs = { index -> if (index == 1) 0L else 10_000L },
            timings = { emptyList() },
        )

        assertEquals(20_000L, timeline.totalDurationMs)
        // Chapter 1 is empty, so a position at ten seconds belongs to chapter 2.
        assertEquals(2, timeline.planIndexAt(10_000L))
    }

    // endregion

    // region fixtures

    /**
     * A timeline whose timings are dense and non-overlapping within each chapter,
     * matching what the renderer produces: one range per spoken unit, in order.
     */
    private fun timelineOf(unitsPerChapter: List<Int>): NarrationTimeline {
        var character = 0
        val perChapterTimings = unitsPerChapter.map { unitCount ->
            var seconds = 0.0
            (0 until unitCount).map {
                val range = ReaderTimingRange(
                    startTime = seconds,
                    endTime = seconds + 3.0,
                    startCharacter = character,
                    endCharacter = character + 60,
                )
                seconds += 3.0
                character += 60
                range
            }
        }

        return NarrationTimeline.of(
            renderedPlanIndices = unitsPerChapter.indices.toList(),
            durationsMs = { index -> (perChapterTimings[index].size * 3_000).toLong() },
            timings = { index -> perChapterTimings[index] },
        )
    }

    // endregion
}
