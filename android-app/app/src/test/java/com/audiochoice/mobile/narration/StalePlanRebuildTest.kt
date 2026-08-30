package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.NarrationPlan
import com.audiochoice.mobile.data.RenderQueue
import com.audiochoice.mobile.data.RenderState
import com.audiochoice.mobile.reader.EpubTextReader
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

/**
 * A book already imported has to pick up a change to how chapters are divided.
 *
 * A stored plan is rebuilt only when [NarrationPlan.PLAN_VERSION] changes or the book's text hash
 * does. Dividing chapters differently alters neither the text nor its hash, so without a version bump
 * the fix ships and the listener never sees it — on precisely the book that exposed the problem,
 * because that is the one already on their phone.
 */
class StalePlanRebuildTest {

    @get:Rule
    val folder = TemporaryFolder()

    /**
     * The version moved past the release that divided Parts as chapters.
     *
     * Asserted as a floor rather than an exact number so a later change can bump it again freely.
     */
    @Test
    fun `the plan version is past the release that treated parts as chapters`() {
        assertTrue(
            "PLAN_VERSION is still 1, so every book already imported keeps the plan it was first " +
                "given and none of the chapter-division fixes reach it",
            NarrationPlan.PLAN_VERSION >= 2,
        )
        assertTrue(
            "the extraction version did not move with it, so a stored plan cannot be told apart " +
                "from one built before front matter and repeated boilerplate were excluded",
            EpubTextReader.NARRATION_EXTRACTION_VERSION >= 2,
        )
    }

    /** A plan stored by an older version is reported stale rather than loaded. */
    @Test
    fun `a plan from an older version is not loaded`() = runBlocking {
        val store = NarrationStore(folder.newFolder())
        val sha = "b".repeat(64)
        store.savePlan(sha, planStoredBy(version = NarrationPlan.PLAN_VERSION - 1))

        val load = store.loadPlan(sha, currentBookTextHash = TEXT_HASH)
        assertTrue(
            "a plan written by an older version was loaded as though current: $load",
            load is PlanLoad.Stale && load.reason == StaleReason.PLAN_VERSION,
        )
    }

    /**
     * Rebuilding drops the audio but keeps the text scan.
     *
     * The audio must go: chapter boundaries moved, so a file stored against chapter five belongs to
     * different words now. The scan must stay: the book's text and every offset in it are unchanged,
     * so re-scanning would send the text away again for an identical answer, and the listener would
     * wait through it.
     */
    @Test
    fun `rebuilding discards the audio and keeps the text scan`() = runBlocking {
        val store = NarrationStore(folder.newFolder())
        val sha = "c".repeat(64)
        store.savePlan(sha, planStoredBy(NarrationPlan.PLAN_VERSION - 1))
        store.saveQueue(sha, RenderQueue(states = listOf(RenderState.RENDERED)))
        store.saveBookText(sha, "The lake lay still.")
        val audio = store.chapterAudioFile(sha, 0)
        audio.parentFile?.mkdirs()
        audio.writeBytes(ByteArray(64))

        store.discardStalePlan(sha, StaleReason.PLAN_VERSION)

        // Checked through the loader, since the plan file's location is the store's own business.
        assertTrue(
            "the stale plan survived",
            store.loadPlan(sha, currentBookTextHash = TEXT_HASH) is PlanLoad.Absent,
        )
        assertEquals(
            "audio recorded against the old chapter boundaries survived, so a chapter would play " +
                "words from a different part of the book",
            0L,
            store.audioBytes(sha),
        )
        assertTrue(
            "the book text was discarded, which forces the text to be sent away again for an " +
                "answer that cannot have changed",
            store.bookText(sha) != null,
        )
    }

    private fun planStoredBy(version: Int) = NarrationPlan(
        planVersion = version,
        inputs = com.audiochoice.mobile.data.PlanInputs(
            sourceSha256 = "d".repeat(64),
            bookTextHash = TEXT_HASH,
            extractionVersion = 1,
            planVersion = version,
            synthesisInputLimit = 3_000,
        ),
        chapters = listOf(
            com.audiochoice.mobile.data.NarrationChapter(
                index = 0,
                title = "Book One",
                startCharacter = 0,
                endCharacter = 19,
            ),
        ),
    )

    private companion object {
        val TEXT_HASH = "e".repeat(64)
    }
}
