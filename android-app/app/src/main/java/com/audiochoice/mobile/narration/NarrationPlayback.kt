package com.audiochoice.mobile.narration

import android.media.MediaPlayer
import com.audiochoice.mobile.data.NarrationPlan
import com.audiochoice.mobile.data.RenderState
import java.io.File

/**
 * Plays a narrated book's rendered chapters, one file at a time.
 *
 * Uses `MediaPlayer` rather than the ExoPlayer session the audiobook player drives, and the reason
 * is worth stating because the opposite choice looks obviously better. The audiobook path needs a
 * media session: lock-screen controls, Bluetooth buttons, background playback of a file that
 * exists in full before playback starts. A narrated book has none of those properties yet -- its
 * chapters appear one at a time while it is being read, so the playlist is still being written as
 * it plays.
 *
 * Sharing the session would have meant teaching the shipping player about a playlist that grows,
 * about items that do not exist yet, and about a duration that changes when a filter changes. That
 * is a large change to the code every existing listener depends on, in exchange for lock-screen
 * controls on an experimental feature. This is the smaller thing that works, and it is replaceable
 * once the feature earns a media session of its own.
 */
class NarrationPlayback(
    private val store: NarrationStore,
    private val onPositionSeconds: (Double) -> Unit,
    private val onChapterFinished: (Int) -> Unit,
    private val onStateChanged: (Boolean) -> Unit,
) {
    private var player: MediaPlayer? = null
    private var currentChapter: Int = -1
    private var cumulativeOffsetSeconds: Double = 0.0

    val isPlaying: Boolean get() = runCatching { player?.isPlaying == true }.getOrDefault(false)

    /**
     * Book_Time, which is the position across the whole book rather than within one chapter's file.
     *
     * The offset is added here, in one place, because chapter timelines are stored
     * chapter-relative: re-rendering one chapter then invalidates no other chapter's timings.
     */
    val positionSeconds: Double
        get() = cumulativeOffsetSeconds +
            (runCatching { player?.currentPosition ?: 0 }.getOrDefault(0) / 1000.0)

    /**
     * Starts or resumes at [chapterIndex].
     *
     * Returns false when that chapter has no audio yet, which is a normal state rather than an
     * error: the renderer may still be working on it.
     */
    fun play(sha256: String, plan: NarrationPlan, chapterIndex: Int, offsetSeconds: Double): Boolean {
        val file = store.chapterAudioFile(sha256, chapterIndex)
        if (!file.isFile || file.length() == 0L) return false

        if (chapterIndex == currentChapter && player != null) {
            runCatching { player?.start() }
            onStateChanged(true)
            return true
        }

        release()
        val created = runCatching {
            MediaPlayer().apply {
                setDataSource(file.absolutePath)
                prepare()
                setOnCompletionListener {
                    onStateChanged(false)
                    onChapterFinished(chapterIndex)
                }
                // A corrupt or truncated file must not leave the reader stuck on "playing". It is
                // reported as finished so the caller can move on or re-render, rather than being
                // swallowed into silence.
                setOnErrorListener { _, _, _ ->
                    onStateChanged(false)
                    onChapterFinished(chapterIndex)
                    true
                }
                start()
            }
        }.getOrNull() ?: return false

        player = created
        currentChapter = chapterIndex
        cumulativeOffsetSeconds = offsetSeconds
        onStateChanged(true)
        onPositionSeconds(offsetSeconds)
        return true
    }

    fun pause() {
        runCatching { player?.takeIf { it.isPlaying }?.pause() }
        onStateChanged(false)
    }

    fun release() {
        runCatching { player?.stop() }
        runCatching { player?.release() }
        player = null
        currentChapter = -1
        onStateChanged(false)
    }

    companion object {
        /**
         * Where a chapter begins in Book_Time, from the durations of the chapters before it.
         *
         * Only rendered chapters contribute. An unrendered chapter adds nothing, which means
         * Book_Time is measured across the audio that exists rather than across the audio that
         * will exist -- so a position stays meaningful while a book is still being made, and the
         * reader's highlight lands on the right words rather than drifting by the length of
         * whatever has not been rendered yet.
         */
        fun chapterOffsetSeconds(
            chapterDurationsMs: List<Long>,
            states: List<RenderState>,
            chapterIndex: Int,
        ): Double {
            var total = 0L
            for (index in 0 until chapterIndex) {
                if (states.getOrNull(index) == RenderState.RENDERED) {
                    total += chapterDurationsMs.getOrElse(index) { 0L }
                }
            }
            return total / 1000.0
        }

        /**
         * The next chapter that has audio to play, at or after [from].
         *
         * "Rendered" is not the same as "has audio", and conflating the two was a real bug that
         * made every book silent. A chapter whose every unit was filtered away -- or which had no
         * prose to begin with, like a title page or a page of chapter rules -- is marked rendered
         * and correctly writes no file: there is nothing to speak, it contributes no duration, and
         * it is complete. Selecting it to play then found no file and failed with no message at
         * all.
         *
         * A zero duration is the marker rather than a filesystem check, because the queue is the
         * record of what was produced and consulting the disk would let the two disagree.
         *
         * A failed chapter is skipped for a different reason: one chapter that could not be
         * synthesized should cost the listener that chapter, not the rest of the novel. They are
         * told about it separately.
         */
        fun nextPlayableChapter(
            states: List<RenderState>,
            chapterDurationsMs: List<Long>,
            from: Int,
        ): Int? = (from until states.size).firstOrNull { index ->
            states[index] == RenderState.RENDERED &&
                chapterDurationsMs.getOrElse(index) { 0L } > 0L
        }

        /**
         * Whether any chapter has audio at all.
         *
         * Distinguished from "nothing rendered" so a book made entirely of silent chapters can be
         * reported as such rather than as a failure to render.
         */
        fun hasAnyAudio(states: List<RenderState>, chapterDurationsMs: List<Long>): Boolean =
            nextPlayableChapter(states, chapterDurationsMs, from = 0) != null

        /** Whether a chapter's audio is present and non-empty on disk. */
        fun hasAudio(file: File): Boolean = file.isFile && file.length() > 0L
    }
}
