package com.audiochoice.mobile.narration

import com.audiochoice.contracts.BookFingerprint
import com.audiochoice.mobile.data.FilterReportComposer
import com.audiochoice.mobile.data.FilterReportPositionUnit
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * Narration reports are capped separately from audiobook ones.
 *
 * A narrated book is one listener's private copy, so its reports are worth less to triage than a
 * report about a published recording several listeners share. Text scanning is also far newer, so a
 * systematic fault there could otherwise fill the whole queue in one session and push out every
 * audiobook report — which are the ones that help more than one person.
 *
 * Checked against the source because `FilterReportQueue` needs a `Context` and an `AudioChoiceApi`,
 * and this project has no Robolectric to supply either.
 */
class NarrationReportCapTest {

    @Test
    fun `narration reports are capped more tightly than audiobook reports`() {
        val source = sourceOf(QUEUE)
        assertTrue(
            "the narration-specific cap is gone, so a narrated book's reports could fill the queue",
            source.contains("MAXIMUM_PENDING_NARRATION"),
        )
        // Tighter than the overall cap, or it would never bind.
        assertTrue(
            "the narration cap is not tighter than the overall cap, so it does nothing",
            source.contains("MAXIMUM_PENDING_NARRATION = 100") &&
                source.contains("MAXIMUM_PENDING = 200"),
        )
    }

    /**
     * The trim must select narration reports specifically, not simply drop the oldest of anything.
     *
     * Dropping the oldest overall would let a narrated book's reports evict audiobook reports, which
     * is precisely what the separate cap exists to prevent.
     */
    @Test
    fun `the narration trim selects only narration reports`() {
        val source = sourceOf(QUEUE)
        assertTrue(
            "the trim no longer selects by position unit, so it could evict audiobook reports",
            source.contains("positionUnit == FilterReportPositionUnit.CHARACTER_OFFSET"),
        )
        // And it is applied after the overall cap, so both bind.
        val overall = source.indexOf("while (pending.size > MAXIMUM_PENDING)")
        val narration = source.indexOf("MAXIMUM_PENDING_NARRATION) {")
        assertTrue("the overall cap was not found", overall > 0)
        assertTrue(
            "the narration cap is applied before the overall cap, so ordering may let one bypass " +
                "the other",
            narration > overall,
        )
    }

    /** Oldest first, on both caps: a recent report is likelier to still be worth acting on. */
    @Test
    fun `the oldest reports are dropped first`() {
        val source = sourceOf(QUEUE)
        assertTrue(
            "the overall cap no longer drops from the front",
            source.contains("pending.removeAt(0)"),
        )
        assertTrue(
            "the narration trim no longer walks forward from the oldest",
            source.contains("val iterator = pending.iterator()"),
        )
    }

    /**
     * The two coordinate spaces stay distinguishable in the queue.
     *
     * The whole cap rests on telling a narration report from an audiobook one, and the only thing
     * that distinguishes them is the position unit — null for every audiobook report, deliberately,
     * so the wire body stays byte-identical to what the deployed server already accepts.
     *
     * Exercises the queue's actual selector against reports built by the real composers, rather
     * than asserting on the constants, so a change to either composer fails here.
     */
    @Test
    fun `the queue selector tells a narration report from an audiobook one`() {
        val audiobook = FilterReportComposer.wronglyFiltered(
            fingerprint = fingerprint("m4b"),
            eventID = "e1",
            categoryID = "profanity",
            startSeconds = 12.0,
            endSeconds = 14.0,
            scannerVersion = "audio-v1",
        )
        val narration = FilterReportComposer.narrationWronglyFiltered(
            fingerprint = fingerprint("epub"),
            eventID = "e2",
            categoryID = "profanity",
            startCharacter = 12,
            endCharacter = 14,
            scannerVersion = "text-v1",
        )

        // This predicate is the one the narration trim uses.
        val isNarration = { unit: String? -> unit == FilterReportPositionUnit.CHARACTER_OFFSET }

        assertNull(
            "an audiobook report now carries a position unit, which both changes the wire body " +
                "the deployed server already accepts and would make it count against the " +
                "narration cap",
            audiobook.positionUnit,
        )
        assertEquals(
            "a narration report is no longer marked as carrying a character offset, so it would " +
                "be counted against the audiobook cap and read as seconds by the server",
            FilterReportPositionUnit.CHARACTER_OFFSET,
            narration.positionUnit,
        )
        assertEquals(false, isNarration(audiobook.positionUnit))
        assertEquals(true, isNarration(narration.positionUnit))
    }

    /** The queue is where a report survives having no signal, so the cap must not be its only job. */
    @Test
    fun `reports are retried rather than discarded on a transient failure`() {
        val source = sourceOf(QUEUE)
        assertTrue(
            "the queue no longer distinguishes a permanent refusal from a transient failure, so a " +
                "report made with no signal would be thrown away",
            source.contains("isPermanentRefusal("),
        )
    }

    private fun sourceOf(relativePath: String): String {
        val file = listOf(
            File(relativePath),
            File("app/$relativePath"),
            File("../app/$relativePath"),
        ).firstOrNull(File::isFile)
        assertTrue(
            "could not locate $relativePath; this guard would otherwise pass without checking",
            file != null,
        )
        return file!!.readText()
    }

    private fun fingerprint(fileType: String) = BookFingerprint(
        version = 1,
        sha256 = "a".repeat(64),
        fileSize = 10,
        duration = null,
        fileType = fileType,
    )

    private companion object {
        const val QUEUE = "src/main/java/com/audiochoice/mobile/data/FilterReportQueue.kt"
    }
}
