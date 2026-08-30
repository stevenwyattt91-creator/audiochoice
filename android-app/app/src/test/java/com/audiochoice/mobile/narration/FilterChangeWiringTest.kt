package com.audiochoice.mobile.narration

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * A filter change must not be written through while rendered audio still contradicts it.
 *
 * `FilterChangeCoordinator` was fully built and tested for some time while nothing called it, so
 * changing a filter mid-book wrote the choice and left the audio alone — leaving a book that said one
 * thing and sounded like another, with nothing to tell the listener which they were hearing. These
 * guard the wiring, which is the part that was missing rather than the arithmetic.
 *
 * Checked against source because `NarrationViewModel` needs a `Context`, a `Store` and an API, and
 * this project has no Robolectric to supply them.
 */
class FilterChangeWiringTest {

    /** The impact check has to happen before anything is persisted. */
    @Test
    fun `setting filter choices consults the coordinator before writing`() {
        val body = functionBody(VIEW_MODEL, "    fun setFilterChoices(")
        assertTrue(
            "setFilterChoices no longer consults FilterChangeCoordinator, so a change that " +
                "invalidates rendered audio would be written silently",
            body.contains("FilterChangeCoordinator.impactOf("),
        )
        // The impact must be computed from the choices in force, not from the incoming ones.
        assertTrue(
            "the impact is not compared against the choices currently in force",
            body.contains("before = current.filterChoices"),
        )
        assertTrue(
            "a change affecting rendered audio is no longer held for confirmation",
            body.contains("pendingFilterChange = PendingFilterChange("),
        )
        // The no-impact case must still write straight through, or every change asks.
        assertTrue(
            "the common no-impact case no longer writes through immediately",
            body.contains("FilterChangeImpact.None -> commitFilterChoices("),
        )
    }

    /**
     * Declining must leave both halves alone.
     *
     * The switches are read from state, which was never changed, so they revert by themselves. What
     * matters is that no audio was discarded on the way to asking.
     */
    @Test
    fun `declining a filter change writes nothing and discards nothing`() {
        val body = functionBody(VIEW_MODEL, "    fun declineFilterChange(")
        assertTrue(
            "declining no longer clears the held change",
            body.contains("pendingFilterChange = null"),
        )
        assertFalse(
            "declining now commits the choice, which is the opposite of declining",
            body.contains("commitFilterChoices("),
        )
        assertFalse(
            "declining now deletes audio",
            body.contains("delete"),
        )
    }

    /**
     * A confirmed filter change commits the choice and then re-renders through the shared path.
     *
     * Discard-requeue-restore is shared with the pronunciation change, because the reason audio is
     * wrong differs between them but what must happen to it does not. Two implementations would drift
     * apart in exactly the ways that lose someone's place in a book.
     */
    @Test
    fun `confirming commits the choice and delegates to the shared re-render`() {
        val body = functionBody(VIEW_MODEL, "    fun confirmFilterChange(")
        val committed = body.indexOf("commitFilterChoices(pending.choices)")
        val rerendered = body.indexOf("rerenderChapters(")
        assertTrue("the confirmed choice is no longer committed", committed > 0)
        assertTrue("the confirmed change no longer re-renders", rerendered > 0)
        assertTrue(
            "the re-render is started before the choice is committed, so the new audio could be " +
                "built from the filters being replaced",
            committed < rerendered,
        )
    }

    /**
     * The position is captured as a character offset before anything is discarded.
     *
     * Re-rendering changes chapter durations, so a Book_Time stops denoting the same words the moment
     * audio is replaced. Capturing it afterwards, or capturing it as a time, both lose the listener's
     * place in a way nothing later can recover.
     */
    @Test
    fun `confirming captures the position as an offset before discarding audio`() {
        val body = functionBody(VIEW_MODEL, "    private fun rerenderChapters(")
        val captured = body.indexOf("characterOffsetOfPosition(")
        val deleted = body.indexOf("store.deleteChapterAudio(")
        assertTrue("the position is no longer captured", captured > 0)
        assertTrue("audio is no longer discarded on confirmation", deleted > 0)
        assertTrue(
            "audio is discarded before the position is captured, which loses the listener's place " +
                "because re-rendering changes the durations the position is measured against",
            captured < deleted,
        )
    }

    /**
     * The in-flight render is *waited for*, not merely cancelled, before audio is discarded.
     *
     * `cancel()` returns before the coroutine has stopped, and the renderer publishes a finished
     * chapter with `partial.renameTo(destination)` — a plain filesystem call containing no
     * cancellation point. A cancelled render can therefore still write a chapter after the delete
     * loop has run, restoring audio built from the filters that were just replaced.
     */
    @Test
    fun `confirming waits for the in-flight render before discarding`() {
        val body = functionBody(VIEW_MODEL, "    private fun rerenderChapters(")
        val joined = body.indexOf("renderJob?.cancelAndJoin()")
        val deleted = body.indexOf("store.deleteChapterAudio(")
        assertTrue(
            "the in-flight render is cancelled without being awaited, so a chapter completing " +
                "mid-flight can publish audio built from the replaced filters after the delete",
            joined > 0,
        )
        assertTrue("audio is no longer discarded", deleted > 0)
        assertTrue(
            "audio is discarded before the in-flight render has stopped",
            joined < deleted,
        )
    }

    /**
     * The position is restored only once the re-render has finished.
     *
     * `renderThenPlay` launches its work and returns. Restoring straight after it reads a timeline
     * in which the discarded chapters are still absent, so the offset either maps to nothing or maps
     * into a surviving chapter whose book time has shifted now that the discarded durations are
     * zero — seeking confidently to the wrong words.
     */
    @Test
    fun `the position is restored only after the re-render completes`() {
        val body = functionBody(VIEW_MODEL, "    private fun rerenderChapters(")
        val started = body.indexOf("renderThenPlay(")
        val awaited = body.indexOf("renderJob?.join()")
        val restored = body.indexOf("restorePositionAfterRerender(")
        assertTrue("the re-render is no longer started", started > 0)
        assertTrue(
            "the re-render is not awaited before the position is restored, so the restore reads a " +
                "timeline that does not yet contain the re-rendered chapters",
            awaited > started,
        )
        assertTrue("the position is no longer restored", restored > 0)
        assertTrue("the position is restored before the re-render is awaited", awaited < restored)
    }

    /** Timings describe a specific recording, so they go when that recording goes. */
    @Test
    fun `discarding a chapter discards its timings too`() {
        val body = functionBody(VIEW_MODEL, "    private fun rerenderChapters(")
        assertTrue(
            "a re-rendered chapter keeps its old timings, so the reader would highlight against " +
                "timings belonging to audio that no longer exists",
            body.contains("store.deleteChapterTimeline("),
        )
        assertTrue(
            "the discarded chapters are not set back to not-rendered, so they would never requeue",
            body.contains("RenderState.NOT_RENDERED"),
        )
        // A stale duration would keep counting toward Narration_Duration for absent audio.
        assertTrue(
            "the discarded chapters keep their durations",
            body.contains("chapterDurationsMs = queue.chapterDurationsMs.mapIndexed"),
        )
    }

    /** The chapter the listener was in is rendered first, so their wait is the shortest possible. */
    @Test
    fun `the chapter at the position is rendered first`() {
        val body = functionBody(VIEW_MODEL, "    private fun rerenderChapters(")
        assertTrue(
            "the re-render no longer prioritises the chapter the listener was in",
            body.contains("playheadChapter = playhead") ||
                body.contains("chapterIndex = playhead"),
        )
    }

    private fun functionBody(relativePath: String, declaration: String): String {
        val file = listOf(
            File(relativePath),
            File("app/$relativePath"),
            File("../app/$relativePath"),
        ).firstOrNull(File::isFile)
        assertTrue(
            "could not locate $relativePath; this guard would otherwise pass without checking",
            file != null,
        )
        val source = file!!.readText()
        val start = source.indexOf(declaration)
        assertTrue("$declaration was not found", start >= 0)
        val end = source.indexOf("\n    }\n", start)
        assertTrue("the end of $declaration was not found", end > start)
        return source.substring(start, end)
    }

    private companion object {
        const val VIEW_MODEL =
            "src/main/java/com/audiochoice/mobile/narration/NarrationViewModel.kt"
    }
}
