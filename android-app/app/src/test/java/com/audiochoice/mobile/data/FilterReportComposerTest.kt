package com.audiochoice.mobile.data

import com.audiochoice.contracts.BookFingerprint
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * What a filter report says, and what it is allowed to contain.
 *
 * Two properties matter. A report has to describe the passage the listener actually heard,
 * which is already behind them by the time they find the button; and it must never carry the
 * content itself, because a listener's audio staying on their device is the promise the whole
 * app rests on.
 *
 * Mirrors the iOS report checks, so triage does not have to know which app a report came from.
 */
class FilterReportComposerTest {
    private val fingerprint = BookFingerprint(
        version = 3,
        sha256 = "a".repeat(64),
        fileSize = 12_345,
        duration = 36_000.0,
        fileType = "m4b",
        workTitle = "King Sorrow",
        author = "Joe Hill",
    )

    @Test
    fun `a missed passage looks back rather than describing an instant`() {
        val report = FilterReportComposer.missedContent(fingerprint, 1234.5, "scanner-7")
        assertEquals(FilterReportKind.MISSED_CONTENT, report.kind)
        assertEquals(1234.5, report.positionSeconds, 0.001)
        assertEquals(FilterReportComposer.LOOK_BACK_SECONDS, report.windowSeconds!!, 0.001)
        assertEquals("scanner-7", report.scannerVersion)
    }

    @Test
    fun `a missed passage claims no event because none fired`() {
        val report = FilterReportComposer.missedContent(fingerprint, 10.0, null)
        assertNull(report.scanEventID)
        assertNull(report.categoryID)
    }

    @Test
    fun `a negative position is clamped`() {
        assertEquals(
            0.0,
            FilterReportComposer.missedContent(fingerprint, -20.0, null).positionSeconds,
            0.001,
        )
    }

    @Test
    fun `a wrongly filtered report names the control that fired`() {
        // Without the event there is no way to tell which control was wrong, which is what
        // turns "it filters the slightest things" into something correctable.
        val report = FilterReportComposer.wronglyFiltered(
            fingerprint = fingerprint,
            eventID = "event-1",
            categoryID = "20000000-0000-0000-0000-000000000001",
            startSeconds = 900.0,
            endSeconds = 906.0,
            scannerVersion = "scanner-7",
        )
        assertEquals(FilterReportKind.WRONGLY_FILTERED, report.kind)
        assertEquals("event-1", report.scanEventID)
        assertEquals(900.0, report.positionSeconds, 0.001)
        assertEquals(6.0, report.windowSeconds!!, 0.001)
    }

    @Test
    fun `an aggregate control reports without inventing an event`() {
        // One word spans many occurrences, so there is no single event to name.
        val report = FilterReportComposer.wronglyFiltered(
            fingerprint = fingerprint,
            eventID = null,
            categoryID = "20000000-0000-0000-0000-000000000001",
            startSeconds = 10.0,
            endSeconds = 11.0,
            scannerVersion = null,
        )
        assertNull(report.scanEventID)
        assertEquals("20000000-0000-0000-0000-000000000001", report.categoryID)
    }

    @Test
    fun `an unreasonably long range is clamped`() {
        val report = FilterReportComposer.wronglyFiltered(
            fingerprint, "event-1", null, 0.0, 100_000.0, null,
        )
        assertTrue(report.windowSeconds!! <= FilterReportComposer.MAXIMUM_WINDOW_SECONDS)
    }

    @Test
    fun `a zero length range still gets a usable window`() {
        val report = FilterReportComposer.wronglyFiltered(
            fingerprint, "event-1", null, 50.0, 50.0, null,
        )
        assertTrue(report.windowSeconds!! >= 1.0)
    }

    @Test
    fun `the kinds serialise as the server names them`() {
        val json = Json { encodeDefaults = true }
        assertTrue(
            json.encodeToString(
                FilterReportComposer.missedContent(fingerprint, 1.0, null),
            ).contains("\"missedContent\""),
        )
        assertTrue(
            json.encodeToString(
                FilterReportComposer.wronglyFiltered(fingerprint, "e", null, 1.0, 2.0, null),
            ).contains("\"wronglyFiltered\""),
        )
    }

    @Test
    fun `a report carries nothing about what was heard`() {
        // Checked against the encoded form, because that is what actually leaves the device.
        // A field added later that carried text would show up here.
        val encoded = Json { encodeDefaults = true }.encodeToString(
            FilterReportComposer.wronglyFiltered(
                fingerprint, "event-1", "20000000-0000-0000-0000-000000000001",
                900.0, 906.0, "scanner-7",
            ),
        )
        assertFalse(encoded.contains("transcript", ignoreCase = true))
        assertFalse(encoded.contains("safeDescription", ignoreCase = true))
        assertFalse(encoded.contains("aggregateDisplay", ignoreCase = true))
    }
}
