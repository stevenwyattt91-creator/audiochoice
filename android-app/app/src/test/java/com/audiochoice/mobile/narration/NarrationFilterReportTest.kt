package com.audiochoice.mobile.narration

import com.audiochoice.contracts.BookFingerprint
import com.audiochoice.contracts.ScanEvent
import com.audiochoice.mobile.data.FilterReportComposer
import com.audiochoice.mobile.data.FilterReportKind
import com.audiochoice.mobile.data.FilterReportPositionUnit
import com.audiochoice.mobile.data.FilterReportRequest
import com.audiochoice.mobile.data.NarrationReportOutcome
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class NarrationFilterReportTest {

    private val json = Json { ignoreUnknownKeys = true; encodeDefaults = true }

    // region the additive field must not change the existing wire shape

    /**
     * The whole reason `positionUnit` is optional with a null default. An imported audiobook's
     * request body has to stay byte-identical to what every shipped client sends, or the field
     * would be a breaking change dressed up as an addition.
     */
    @Test
    fun `an audiobook report serialises without the position unit`() {
        val report = FilterReportComposer.missedContent(
            fingerprint = fingerprint("m4b"),
            positionSeconds = 1_234.5,
            scannerVersion = "v1",
        )

        val body = json.encodeToString(FilterReportRequest.serializer(), report)

        assertFalse(
            "an audiobook report now carries positionUnit, which changes the wire shape",
            body.contains("positionUnit"),
        )
        assertNull(report.positionUnit)
        assertTrue(body.contains("\"positionSeconds\":1234.5"))
    }

    /**
     * A body from an older client, with no `positionUnit` at all, must still deserialise and
     * mean seconds.
     */
    @Test
    fun `a body with no position unit deserialises as seconds`() {
        val legacy = """
            {"fingerprint":{"version":1,"sha256":"${"a".repeat(64)}","fileSize":10,
            "fileType":"m4b"},"kind":"missedContent","positionSeconds":42.0,
            "windowSeconds":20.0}
        """.trimIndent().replace("\n", "")

        val decoded = json.decodeFromString(FilterReportRequest.serializer(), legacy)

        assertNull(decoded.positionUnit)
        assertEquals(42.0, decoded.positionSeconds, 0.0)
    }

    // endregion

    // region narration reports

    @Test
    fun `a narration report carries the character offset unit`() {
        val report = FilterReportComposer.narrationMissedContent(
            fingerprint = fingerprint("epub"),
            characterOffset = 84_000,
            scannerVersion = "text-v1",
        )

        assertEquals(FilterReportPositionUnit.CHARACTER_OFFSET, report.positionUnit)
        assertEquals(84_000.0, report.positionSeconds, 0.0)
        assertEquals(FilterReportKind.MISSED_CONTENT, report.kind)
        // The look-back is a character window, not a converted time window.
        assertEquals(
            FilterReportComposer.LOOK_BACK_CHARACTERS.toDouble(),
            report.windowSeconds!!,
            0.0,
        )
    }

    /**
     * The look-back has to be in characters. Converting to seconds and back would reintroduce
     * exactly the ambiguity the position unit exists to remove.
     */
    @Test
    fun `the narration look-back is a character window not a time window`() {
        assertFalse(
            "the narration window matches the audio window, so it was probably copied",
            FilterReportComposer.LOOK_BACK_CHARACTERS.toDouble() ==
                FilterReportComposer.LOOK_BACK_SECONDS,
        )
        assertTrue(FilterReportComposer.LOOK_BACK_CHARACTERS > 100)
    }

    @Test
    fun `a wrongly-filtered narration report carries the event identifiers`() {
        val report = FilterReportComposer.narrationWronglyFiltered(
            fingerprint = fingerprint("epub"),
            eventID = "event-1",
            categoryID = CATEGORY,
            startCharacter = 500,
            endCharacter = 620,
            scannerVersion = "text-v1",
        )

        assertEquals(FilterReportPositionUnit.CHARACTER_OFFSET, report.positionUnit)
        assertEquals(500.0, report.positionSeconds, 0.0)
        assertEquals(120.0, report.windowSeconds!!, 0.0)
        assertEquals("event-1", report.scanEventID)
        assertEquals(CATEGORY, report.categoryID)
        assertEquals(FilterReportKind.WRONGLY_FILTERED, report.kind)
    }

    /** A very long flagged passage describes the book, not a moment in it. */
    @Test
    fun `an enormous span is capped`() {
        val report = FilterReportComposer.narrationWronglyFiltered(
            fingerprint("epub"), null, null, 0, 500_000, "text-v1",
        )
        assertEquals(
            FilterReportComposer.MAXIMUM_WINDOW_CHARACTERS.toDouble(),
            report.windowSeconds!!,
            0.0,
        )
    }

    /** An inverted or empty span still has to produce a usable window. */
    @Test
    fun `an inverted span produces a minimum window`() {
        val report = FilterReportComposer.narrationWronglyFiltered(
            fingerprint("epub"), null, null, 500, 500, "text-v1",
        )
        assertEquals(1.0, report.windowSeconds!!, 0.0)
    }

    // endregion

    // region choosing the containing event

    /**
     * Several events legitimately contain one offset: a profanity inside a scene inside a
     * chapter-scale flag. The lowest start wins, which is both the widest containing passage
     * and a deterministic tie-break, so two reports of the same moment name the same event.
     */
    @Test
    fun `the lowest start offset wins when several events contain the offset`() {
        val events = listOf(
            event("wide", 0, 5_000),
            event("scene", 800, 1_200),
            event("word", 1_000, 1_006),
        )

        val chosen = FilterReportComposer.containingEvent(events, characterOffset = 1_002)

        assertEquals("wide", chosen?.id)
    }

    /** Equal starts fall through to the shorter span, then to the identifier. */
    @Test
    fun `equal starts are broken deterministically`() {
        val events = listOf(event("b", 100, 900), event("a", 100, 200))
        assertEquals("a", FilterReportComposer.containingEvent(events, 150)?.id)
    }

    @Test
    fun `an offset in no event chooses nothing`() {
        val events = listOf(event("one", 0, 100), event("two", 500, 600))
        assertNull(FilterReportComposer.containingEvent(events, characterOffset = 300))
    }

    /** The end offset is exclusive, matching every other range in the narration code. */
    @Test
    fun `the end offset is exclusive`() {
        val events = listOf(event("one", 0, 100))
        assertEquals("one", FilterReportComposer.containingEvent(events, 99)?.id)
        assertNull(FilterReportComposer.containingEvent(events, 100))
    }

    @Test
    fun `no events chooses nothing`() {
        assertNull(FilterReportComposer.containingEvent(emptyList(), 0))
    }

    // endregion

    // region the two no-mapping cases

    /**
     * A moment that maps to no position in the text sends nothing.
     *
     * Happens across a gap between rendered chapters, or before anything is rendered. Guessing an
     * offset from a nearby chapter would point triage at the wrong passage — worse than no report,
     * because it arrives looking like evidence.
     */
    @Test
    fun `a moment with no text behind it sends nothing`() {
        val outcome = FilterReportComposer.narrationReport(
            fingerprint = fingerprint("epub"),
            bookTimeSeconds = 900.0,
            enabledEvents = listOf(event("one", 0, 100)),
            scannerVersion = "text-v1",
            // No timing covers this moment.
            characterForTime = { null },
        )
        assertEquals(NarrationReportOutcome.NoTextAtThisMoment, outcome)
    }

    /**
     * An offset that no enabled filter covers sends nothing either, and changes no filter choice.
     *
     * The listener has reported a passage nothing removed, so there is no control to name. They
     * asked a question; treating it as an instruction would silently alter their filters.
     */
    @Test
    fun `an offset no filter covers sends nothing`() {
        val outcome = FilterReportComposer.narrationReport(
            fingerprint = fingerprint("epub"),
            bookTimeSeconds = 10.0,
            enabledEvents = listOf(event("one", 0, 100), event("two", 500, 600)),
            scannerVersion = "text-v1",
            characterForTime = { 300 },
        )
        assertEquals(NarrationReportOutcome.NothingFilteredHere, outcome)
    }

    /** A reportable moment names the control that removed the passage. */
    @Test
    fun `a covered offset produces a report naming the event`() {
        val outcome = FilterReportComposer.narrationReport(
            fingerprint = fingerprint("epub"),
            bookTimeSeconds = 10.0,
            enabledEvents = listOf(event("wide", 0, 5_000), event("word", 1_000, 1_006)),
            scannerVersion = "text-v1",
            characterForTime = { 1_002 },
        )
        val ready = outcome as NarrationReportOutcome.Ready
        // The widest containing passage, which is the deterministic tie-break.
        assertEquals("wide", ready.event.id)
        assertEquals("wide", ready.request.scanEventID)
        assertEquals(
            FilterReportPositionUnit.CHARACTER_OFFSET,
            ready.request.positionUnit,
        )
        assertEquals(0.0, ready.request.positionSeconds, 0.0)
    }

    /** An empty event list is the same case as an uncovered offset, not a crash. */
    @Test
    fun `no events at all sends nothing`() {
        assertEquals(
            NarrationReportOutcome.NothingFilteredHere,
            FilterReportComposer.narrationReport(
                fingerprint = fingerprint("epub"),
                bookTimeSeconds = 1.0,
                enabledEvents = emptyList(),
                scannerVersion = null,
                characterForTime = { 0 },
            ),
        )
    }

    // endregion

    // region fixtures

    private fun fingerprint(fileType: String) = BookFingerprint(
        version = 1,
        sha256 = "a".repeat(64),
        fileSize = 10,
        duration = null,
        fileType = fileType,
    )

    private fun event(id: String, start: Int, end: Int) = ScanEvent(
        id = id,
        startTime = start.toDouble(),
        endTime = end.toDouble(),
        categoryID = CATEGORY,
        groupID = GROUP,
        eventID = EVENT,
        confidence = 1.0,
        stableKey = "stable-$id",
        safeDescription = "Something occurs",
    )

    private companion object {
        const val CATEGORY = "21000000-0000-0000-0000-000000000000"
        const val GROUP = "21000000-0000-0000-0000-000000000001"
        const val EVENT = "21000000-0000-0000-0000-000000000101"
    }
}
