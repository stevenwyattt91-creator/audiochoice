package com.audiochoice.mobile.narration

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * A narrated book's flagged positions must never be presented as timestamps.
 *
 * Its scan events carry character offsets in the same `startTime` field an audiobook uses for
 * seconds. That reuse is what lets the entire existing filter stack work unchanged, and it is also
 * the most dangerous thing about the contract: the audiobook filter screen formats that field as
 * `mm:ss`, so a narrated book's offset of 84,000 would render as "23:20:00" — a number that looks
 * authoritative and means nothing.
 *
 * Checked against the source because the formatting lives in a private composable and this project
 * has no Robolectric, so the screen cannot be rendered in a JVM test.
 */
class NarratedPositionLabelTest {

    /**
     * The narration filter surface must not reach for the audiobook time formatter.
     *
     * `formatTime` is right there in the same file and takes exactly the value the event carries,
     * which is what makes this mistake easy rather than unlikely.
     */
    @Test
    fun `the narration filter dialog never formats an offset as a timestamp`() {
        val body = functionBody("private fun NarrationFilterDialog(")
        assertFalse(
            "the narration filter dialog formats a character offset with formatTime, which " +
                "renders offset 84,000 as \"23:20:00\"",
            body.contains("formatTime("),
        )
        assertTrue(
            "the narration filter dialog no longer presents a position at all",
            body.contains("narratedPositionLabel("),
        )
    }

    /**
     * A percentage is what a character offset honestly tells a listener, and unlike a time it stays
     * correct while a book is only part-rendered — a time does not exist until the audio does.
     */
    @Test
    fun `the position label is a share of the book rather than a duration`() {
        val body = functionBody("private fun narratedPositionLabel(")
        assertTrue(
            "the label no longer expresses a share of the book",
            body.contains("through the book"),
        )
        assertFalse(
            "the label reintroduced time formatting",
            body.contains("formatTime(") || body.contains(":%02d"),
        )
        // Guarded against a book with no known length, and against an offset past its end.
        assertTrue("an unknown book length is not handled", body.contains("bookTextLength <= 0"))
        assertTrue("the share is not clamped", body.contains("coerceIn(0.0, 1.0)"))
    }

    /**
     * The audiobook screen keeps its timestamps.
     *
     * Asserted alongside so a well-meaning change cannot "fix" the narrated case by removing time
     * formatting from the path where seconds are exactly what the field means.
     */
    @Test
    fun `the audiobook filter screen still shows real timestamps`() {
        assertTrue(
            "the audiobook filter hierarchy no longer formats its event times, where the value " +
                "genuinely is seconds",
            functionBody("private fun FilterHierarchyContent(").contains("formatTime("),
        )
    }

    /** Individual controls exist, so a listener can switch off one word, not just a category. */
    @Test
    fun `individual event controls are offered for a narrated book`() {
        val body = functionBody("private fun NarrationFilterDialog(")
        assertTrue(
            "a narrated book offers no individual event controls, so one repeated word cannot be " +
                "switched off on its own",
            body.contains("child.events.forEach"),
        )
        // An aggregate control and a single event are stored in different sets; conflating them
        // would silently fail to disable a repeated word.
        assertTrue(
            "aggregate and individual controls are not stored separately",
            body.contains("if (event.aggregate) aggregates else events"),
        )
    }

    private fun functionBody(declaration: String): String {
        val file = listOf(
            File(APP),
            File("app/$APP"),
            File("../app/$APP"),
        ).firstOrNull(File::isFile)
        assertTrue(
            "could not locate AudioChoiceApp.kt; this guard would otherwise pass without checking",
            file != null,
        )
        val source = file!!.readText()
        val start = source.indexOf(declaration)
        assertTrue("$declaration was not found", start >= 0)
        val end = source.indexOf("\n}", start)
        assertTrue("the end of $declaration was not found", end > start)
        return source.substring(start, end)
    }

    private companion object {
        const val APP = "src/main/java/com/audiochoice/mobile/ui/AudioChoiceApp.kt"
    }
}
