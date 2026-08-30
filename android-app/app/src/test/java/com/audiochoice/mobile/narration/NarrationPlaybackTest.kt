package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.RenderState
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class NarrationPlaybackTest {

    // region where a chapter begins in Book_Time

    /**
     * Only rendered chapters contribute to the offset.
     *
     * Book_Time is measured across the audio that exists, not across the audio that will exist.
     * Counting an unrendered chapter's estimated length would drift every later position by
     * however much has not been made yet, and the reader's highlight would land on the wrong
     * words -- growing worse the further into the book the listener got.
     */
    @Test
    fun `an unrendered chapter contributes nothing to the offset`() {
        val durations = listOf(60_000L, 60_000L, 60_000L, 60_000L)
        val states = listOf(
            RenderState.RENDERED,
            RenderState.NOT_RENDERED,
            RenderState.RENDERED,
            RenderState.RENDERED,
        )

        // Chapter 3 sits after chapters 0 and 2, which have audio. Chapter 1 does not.
        assertEquals(
            120.0,
            NarrationPlayback.chapterOffsetSeconds(durations, states, chapterIndex = 3),
            0.001,
        )
    }

    @Test
    fun `the first chapter starts at zero`() {
        assertEquals(
            0.0,
            NarrationPlayback.chapterOffsetSeconds(
                listOf(60_000L), listOf(RenderState.RENDERED), chapterIndex = 0,
            ),
            0.0,
        )
    }

    /** A failed chapter has no audio, so it contributes nothing either. */
    @Test
    fun `a failed chapter contributes nothing`() {
        val offset = NarrationPlayback.chapterOffsetSeconds(
            listOf(30_000L, 30_000L, 30_000L),
            listOf(RenderState.RENDERED, RenderState.RENDER_FAILED, RenderState.RENDERED),
            chapterIndex = 2,
        )
        assertEquals(30.0, offset, 0.001)
    }

    /** A duration list shorter than the state list must not throw. */
    @Test
    fun `a missing duration is treated as zero rather than throwing`() {
        val offset = NarrationPlayback.chapterOffsetSeconds(
            chapterDurationsMs = emptyList(),
            states = List(4) { RenderState.RENDERED },
            chapterIndex = 3,
        )
        assertEquals(0.0, offset, 0.0)
    }

    // endregion

    // region choosing what to play next

    /**
     * A chapter that could not be synthesized is stepped over rather than ending the book.
     *
     * One bad chapter should cost the listener that chapter, not the rest of the novel. They are
     * told about the failure separately, and can ask for it again.
     */
    @Test
    fun `a failed chapter is skipped rather than ending the book`() {
        val states = listOf(
            RenderState.RENDERED,
            RenderState.RENDER_FAILED,
            RenderState.RENDERED,
        )
        assertEquals(
            2,
            NarrationPlayback.nextPlayableChapter(states, listOf(60_000, 0, 60_000), from = 1),
        )
    }

    @Test
    fun `playback resumes at the first chapter that has audio`() {
        val states = listOf(
            RenderState.NOT_RENDERED,
            RenderState.NOT_RENDERED,
            RenderState.RENDERED,
        )
        assertEquals(
            2,
            NarrationPlayback.nextPlayableChapter(states, listOf(0, 0, 60_000), from = 0),
        )
    }

    /**
     * A rendered chapter with no audio is skipped.
     *
     * This was a real bug that made every book silent. The front of a book is routinely a title
     * page and a copyright notice: those have no prose, so they render correctly as nothing --
     * no file, no duration, complete. Treating "rendered" as "has audio" then selected chapter
     * zero, found no file, and failed with no message at all.
     */
    @Test
    fun `a rendered chapter with no audio is not playable`() {
        val states = List(3) { RenderState.RENDERED }
        // Chapter 0 is a title page, chapter 1 a page of chapter rules, chapter 2 real prose.
        val durations = listOf(0L, 0L, 90_000L)

        assertEquals(
            2,
            NarrationPlayback.nextPlayableChapter(states, durations, from = 0),
        )
    }

    /** A book of nothing but silent chapters is reported as such, not as a render failure. */
    @Test
    fun `a book with only silent chapters has no audio at all`() {
        val states = List(3) { RenderState.RENDERED }
        assertNull(
            NarrationPlayback.nextPlayableChapter(states, listOf(0L, 0L, 0L), from = 0),
        )
        assertFalse(
            NarrationPlayback.hasAnyAudio(states, listOf(0L, 0L, 0L)),
        )
        assertTrue(
            NarrationPlayback.hasAnyAudio(states, listOf(0L, 0L, 1L)),
        )
    }

    /** Nothing left to play is a real answer, not an error. */
    @Test
    fun `no remaining audio reports nothing`() {
        assertNull(
            NarrationPlayback.nextPlayableChapter(
                listOf(RenderState.RENDERED, RenderState.NOT_RENDERED),
                listOf(60_000, 0),
                from = 1,
            ),
        )
        assertNull(NarrationPlayback.nextPlayableChapter(emptyList(), emptyList(), from = 0))
        // Past the end of the book.
        assertNull(
            NarrationPlayback.nextPlayableChapter(
                listOf(RenderState.RENDERED), listOf(60_000), from = 5,
            ),
        )
    }

    /** The chapter being played is itself a candidate, so resuming does not skip forward. */
    @Test
    fun `the starting chapter is itself playable`() {
        assertEquals(
            1,
            NarrationPlayback.nextPlayableChapter(
                List(3) { RenderState.RENDERED }, List(3) { 60_000L }, from = 1,
            ),
        )
    }

    // endregion

    // region the offset and the next chapter agree

    /**
     * The two functions have to describe the same book. If the offset counted chapters the
     * playable search skipped, a listener crossing a failed chapter would jump by its length.
     */
    @Test
    fun `crossing a failed chapter does not move the position by its length`() {
        val durations = listOf(60_000L, 60_000L, 60_000L)
        val states = listOf(
            RenderState.RENDERED, RenderState.RENDER_FAILED, RenderState.RENDERED,
        )

        val next = NarrationPlayback.nextPlayableChapter(states, durations, from = 1)
        assertEquals(2, next)

        // Chapter 2 begins where chapter 0 ended: one minute, not two.
        assertEquals(
            60.0,
            NarrationPlayback.chapterOffsetSeconds(durations, states, next!!),
            0.001,
        )
    }

    // endregion
}
