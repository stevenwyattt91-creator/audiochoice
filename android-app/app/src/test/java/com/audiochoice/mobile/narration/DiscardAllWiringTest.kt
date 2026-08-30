package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.RenderState
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * Reclaiming a book's audio frees space and costs only waiting.
 *
 * Everything cheap to keep must survive: the reading plan, the word timings, the scan results, the
 * pronunciation rules and the listener's place. Only the audio is large, and only the audio can be
 * rebuilt from what remains — so anything else lost here is lost for no gain.
 */
class DiscardAllWiringTest {

    /**
     * A failed chapter stays failed.
     *
     * It has no audio to reclaim, so calling it not-rendered would quietly discard the record that it
     * could not be made and present it as merely pending.
     */
    @Test
    fun `a failed chapter is not relabelled as pending`() {
        val body = functionBody(VIEW_MODEL, "    fun discardAllAudio(")
        assertTrue(
            "every state is reset rather than only the rendered ones, which turns a chapter that " +
                "could not be made into one that merely has not been made yet",
            body.contains("if (state == RenderState.RENDERED) RenderState.NOT_RENDERED else state"),
        )
    }

    /** Timings survive, which is what makes reclaiming space cheap to undo. */
    @Test
    fun `discarding audio keeps the timings, the plan and the scan results`() {
        val body = functionBody(VIEW_MODEL, "    fun discardAllAudio(")
        assertFalse(
            "discarding audio now deletes the word timings, so re-rendering could not restore the " +
                "listener's place and the reader could not highlight",
            body.contains("deleteChapterTimeline("),
        )
        assertFalse(
            "discarding audio now deletes the text scan, which would send the book's text away " +
                "again to reclaim space",
            body.contains("deleteTextScan("),
        )
        assertFalse(
            "discarding audio now deletes the whole book",
            body.contains("deleteBook("),
        )
        assertTrue(
            "discarding audio no longer removes the audio",
            body.contains("store.deleteAllChapterAudio("),
        )
    }

    /**
     * Playback stops before the files under it are removed.
     *
     * A player holding a handle to a deleted file is how this becomes a crash rather than a
     * reclamation.
     */
    @Test
    fun `playback is stopped before the audio is deleted`() {
        val body = functionBody(VIEW_MODEL, "    fun discardAllAudio(")
        val released = body.indexOf("playback?.release()")
        val deleted = body.indexOf("store.deleteAllChapterAudio(")
        assertTrue("playback is no longer released", released > 0)
        assertTrue("the audio is no longer deleted", deleted > 0)
        assertTrue(
            "the audio is deleted while the player still holds it open",
            released < deleted,
        )
    }

    /**
     * The in-flight render is awaited, not merely cancelled, before the wipe.
     *
     * `cancel()` returns before the coroutine has stopped, and the renderer publishes a finished
     * chapter with a plain `renameTo` containing no cancellation point. A cancelled pass can
     * therefore write a chapter after the wipe, leaving a chapter that reports itself reclaimed while
     * still occupying the space the listener was told they had freed.
     */
    @Test
    fun `the in-flight render is awaited before the audio is wiped`() {
        val body = functionBody(VIEW_MODEL, "    fun discardAllAudio(")
        val joined = body.indexOf("renderJob?.cancelAndJoin()")
        val deleted = body.indexOf("store.deleteAllChapterAudio(")
        assertTrue(
            "the in-flight render is cancelled without being awaited, so a chapter completing " +
                "mid-flight can republish audio after the wipe",
            joined > 0,
        )
        assertTrue("the audio is no longer wiped", deleted > 0)
        assertTrue("the audio is wiped before the render has stopped", joined < deleted)
    }
    /** Nothing is discarded until the listener confirms. */
    @Test
    fun `offering the discard deletes nothing`() {
        val body = functionBody(VIEW_MODEL, "    fun offerDiscardAllAudio(")
        assertFalse(
            "offering the discard already deletes the audio, so the confirmation is decorative",
            body.contains("delete"),
        )
        assertTrue(
            "the offer no longer reports what it would reclaim",
            body.contains("NarrationStorage.discardEstimate("),
        )
    }

    /** The figure is re-read from the files, so it cannot drift against what is on disk. */
    @Test
    fun `the storage figure is re-read after rendering and after discarding`() {
        val source = sourceOf(VIEW_MODEL)
        assertTrue(
            "the storage figure is no longer refreshed, so it would stay at whatever it was when " +
                "the book was opened",
            source.split("refreshAudioBytes()").size - 1 >= 3,
        )
        assertTrue(
            "the figure is derived from a counter rather than read from the files, which drifts " +
                "against a directory that eviction, a discard and a re-render all write to",
            functionBody(VIEW_MODEL, "    private fun refreshAudioBytes(")
                .contains("store.audioBytes("),
        )
    }

    /**
     * The reported cost counts only chapters that actually have audio.
     *
     * Exercised for real, since this arithmetic is what the listener decides on.
     */
    @Test
    fun `the estimate counts only chapters holding audio`() {
        val estimate = NarrationStorage.discardEstimate(
            audioBytes = 5L * 1_048_576,
            states = listOf(
                RenderState.RENDERED,
                RenderState.RENDER_FAILED,
                RenderState.NOT_RENDERED,
                RenderState.RENDERED,
            ),
        )
        assertEquals(2, estimate.chaptersNeedingRerender)
        assertEquals(5L * 1_048_576, estimate.reclaimableBytes)
        // Rounded down for display, so it never promises more space than it frees.
        assertEquals(5L, NarrationStorage.displayMegabytes(estimate.reclaimableBytes))
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

    private fun functionBody(relativePath: String, declaration: String): String {
        val source = sourceOf(relativePath)
        val start = source.indexOf(declaration)
        assertTrue("$declaration was not found", start >= 0)
        val end = source.indexOf("\n    }", start)
        assertTrue("the end of $declaration was not found", end > start)
        return source.substring(start, end)
    }

    private companion object {
        const val VIEW_MODEL =
            "src/main/java/com/audiochoice/mobile/narration/NarrationViewModel.kt"
    }
}
