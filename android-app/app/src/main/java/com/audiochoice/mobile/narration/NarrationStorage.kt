package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.NarrationChapter
import com.audiochoice.mobile.data.RenderState
import com.audiochoice.mobile.data.VoiceKind

/** Whether there is room to render, and how much is missing if not. */
sealed interface StorageVerdict {

    data class Sufficient(val estimatedBytes: Long, val freeBytes: Long) : StorageVerdict

    /**
     * Not enough room. Every chapter stays unrendered.
     *
     * The plan and the filter results are kept: they cost nothing to hold, and discarding them
     * would mean a listener who frees up space pays for a scan again.
     */
    data class Insufficient(
        val estimatedBytes: Long,
        val freeBytes: Long,
        val shortfallBytes: Long,
    ) : StorageVerdict {
        val shortfallMegabytes: Long get() = NarrationStorage.toMegabytes(shortfallBytes)
    }
}

/** What a discard-all would reclaim, presented before anything is deleted. */
data class DiscardEstimate(
    val reclaimableBytes: Long,
    val chaptersNeedingRerender: Int,
) {
    val reclaimableMegabytes: Long get() = NarrationStorage.toMegabytes(reclaimableBytes)
}

/**
 * Storage arithmetic for narration audio.
 *
 * Pure, so the estimate can be compared against a real encode without a device. The numbers
 * here decide whether a listener is told "not enough room" before waiting through a render
 * that would fail, which makes being roughly right beforehand worth more than being exactly
 * right afterwards.
 */
object NarrationStorage {

    /**
     * Free space never consumed by narration audio.
     *
     * A phone that fills completely stops being able to do anything -- take a photo, receive a
     * message, apply an update. A gigabyte is enough headroom that narration is never the
     * reason a device becomes unusable, and small enough that a book still fits on a fairly
     * full phone.
     */
    const val STORAGE_RESERVE_BYTES = 1_024L * 1_024 * 1_024

    /**
     * The bitrate the encoder is asked for.
     *
     * Not what comes out: Opus is variable-rate, so a request for 32 kbps produces less than
     * that on speech. Use [EFFECTIVE_BITRATE_BITS_PER_SECOND] for estimating file sizes.
     */
    const val AUDIO_BITRATE_BITS_PER_SECOND = 32_000L

    /**
     * What 32 kbps Opus actually produces on narration, measured.
     *
     * 29.7 kbps across three Polly generative voices on 1,080 characters of mixed narration and
     * dialogue, 2026-08-29. Rounded to 30,000. Estimating with the requested rate instead would
     * over-predict every book by about 8 percent, which sounds harmless until it is the
     * difference between offering to render a book and refusing to.
     */
    const val EFFECTIVE_BITRATE_BITS_PER_SECOND = 30_000L

    /**
     * Characters spoken per second, per engine.
     *
     * **PREMIUM is measured.** 17.98 characters per second, averaged over three Polly generative
     * voices reading 1,080 characters of mixed narration and dialogue on 2026-08-29 -- Ruth
     * 17.60, Matthew 19.74, Danielle 16.61. The spread between voices is about 19 percent, which
     * is why the average of three is used rather than one voice's figure.
     *
     * The first version of this file guessed 13.5, reasoning from 150 words a minute. That was
     * wrong by a third: the real engine reads faster than an unhurried human narrator, and the
     * guess would have over-predicted storage for every premium book by roughly 33 percent. It
     * is recorded here because it is exactly the kind of plausible-sounding derivation that
     * survives review, and the only thing that caught it was synthesizing a passage and timing
     * the result.
     *
     * **SYSTEM is measured.** 18.4 characters per second on a Samsung SM-S936U running Android
     * 16, 2026-08-29, over the fixed benchmark passage in `OnDeviceRate` -- 515 characters, 67
     * voices available, `en-US-language`. Worth noting that the device's own engine speaks at
     * almost exactly the rate Polly does (18.4 against 18.0), which is why the two constants sit
     * so close together: that turned out to be a fact about speech rather than a coincidence.
     *
     * The earlier estimate here was 16.0, which was 13 percent low. Smaller than the premium
     * error, and in the same direction, because both came from assuming a synthetic narrator
     * reads at a considered human pace. It does not.
     *
     * **LOCAL_NEURAL is still an estimate.** No on-device neural engine has been measured, so it
     * is set to the measured system figure rather than to a guess of its own -- if the two
     * engines differ it will be because of the model, and inventing a difference before seeing
     * one would just be a second wrong number.
     */
    fun charactersPerSecond(kind: VoiceKind): Double = when (kind) {
        VoiceKind.SYSTEM -> 18.4
        VoiceKind.LOCAL_NEURAL -> 18.4
        VoiceKind.PREMIUM -> 18.0
    }

    /** True only where the rate above rests on a measurement rather than a derivation. */
    fun rateIsMeasured(kind: VoiceKind): Boolean = when (kind) {
        VoiceKind.SYSTEM, VoiceKind.PREMIUM -> true
        VoiceKind.LOCAL_NEURAL -> false
    }

    /** Estimated bytes for [characterCount] characters of spoken text. */
    fun estimateBytes(characterCount: Int, kind: VoiceKind): Long {
        if (characterCount <= 0) return 0L
        val seconds = characterCount / charactersPerSecond(kind)
        return (seconds * EFFECTIVE_BITRATE_BITS_PER_SECOND / 8).toLong()
    }

    /**
     * Estimated bytes for everything still to be rendered.
     *
     * Counts only chapters that are not already rendered, so re-checking part-way through a
     * book does not demand room for audio that already exists.
     */
    fun estimateRemainingBytes(
        chapters: List<NarrationChapter>,
        states: List<RenderState>,
        kind: VoiceKind,
    ): Long = chapters.indices.sumOf { index ->
        if (states.getOrNull(index) == RenderState.RENDERED) {
            0L
        } else {
            estimateBytes(spokenCharacters(chapters[index]), kind)
        }
    }

    /** Characters a chapter will actually speak, after filtering removed what it removed. */
    fun spokenCharacters(chapter: NarrationChapter): Int =
        chapter.units.sumOf { it.sourceCharacters.length }

    /**
     * Whether rendering may begin.
     *
     * The reserve is subtracted from free space before the comparison, so a book that would
     * fit only by eating into the reserve is refused rather than started and stopped part-way.
     */
    fun verdictFor(estimatedBytes: Long, freeBytes: Long): StorageVerdict {
        val usable = freeBytes - STORAGE_RESERVE_BYTES
        return if (estimatedBytes <= usable) {
            StorageVerdict.Sufficient(estimatedBytes, freeBytes)
        } else {
            StorageVerdict.Insufficient(
                estimatedBytes = estimatedBytes,
                freeBytes = freeBytes,
                // Reported as what the listener must free, which is the number they can act
                // on. Never negative, so a shortfall of zero cannot be reported as a surplus.
                shortfallBytes = (estimatedBytes - usable).coerceAtLeast(0L),
            )
        }
    }

    /**
     * Whether an in-progress render must stop now.
     *
     * Checked before each chapter and periodically inside one, because a long chapter can
     * take minutes and free space is not the renderer's alone to consume: a download or a
     * photo can take the reserve while a chapter is being written.
     */
    fun mustStopRendering(freeBytes: Long): Boolean = freeBytes <= STORAGE_RESERVE_BYTES

    /** How often free space is measured inside a single chapter. */
    const val FREE_SPACE_CHECK_INTERVAL_MS = 30_000L

    /** How long a stop may take once the reserve is reached. */
    const val STOP_DEADLINE_MS = 5_000L

    /** What discarding every chapter's audio would reclaim. */
    fun discardEstimate(
        audioBytes: Long,
        states: List<RenderState>,
    ): DiscardEstimate = DiscardEstimate(
        reclaimableBytes = audioBytes,
        // A failed chapter needs rendering again too, but it has no audio to reclaim, so it
        // is not counted here: this number answers "what will I have to wait for again".
        chaptersNeedingRerender = states.count { it == RenderState.RENDERED },
    )

    fun toMegabytes(bytes: Long): Long = (bytes + 1_048_575) / 1_048_576

    /** Megabytes for display, rounded down, which is what a size reads as everywhere else. */
    fun displayMegabytes(bytes: Long): Long = bytes / 1_048_576
}

/**
 * Which chapters' audio may be deleted as playback moves forward.
 *
 * Off unless a listener turns it on for a book. Someone who waited through a render did so to
 * have the audio, and deciding on their behalf to throw it away would be the wrong default
 * even when storage is tight.
 */
object NarrationEviction {

    /**
     * Chapters kept behind the playhead.
     *
     * Two is enough to cover the ordinary reasons someone goes back -- a missed sentence, a
     * re-listen, a chapter boundary crossed by accident -- without holding a whole book.
     */
    const val CHAPTERS_KEPT_BEHIND = 2

    /**
     * Chapters whose audio may be deleted with the playhead in [currentChapterIndex].
     *
     * A chapter holding a bookmark is never evicted. A bookmark is a listener saying "come
     * back here", and making them wait through a re-render to honour it would be a poor
     * answer to an explicit instruction.
     *
     * Deleting audio sets the chapter to not rendered and keeps its timeline, which is why
     * timelines are stored per chapter and relative: the timings stay valid, so the reader
     * still knows where the words are even with the audio gone.
     */
    fun evictableChapters(
        states: List<RenderState>,
        currentChapterIndex: Int,
        bookmarkedChapters: Set<Int>,
    ): List<Int> = states.indices.filter { index ->
        states[index] == RenderState.RENDERED &&
            index < currentChapterIndex - CHAPTERS_KEPT_BEHIND &&
            index !in bookmarkedChapters
    }

    /**
     * Which chapter each bookmark falls in.
     *
     * Bookmarks are recorded in Book_Time, and eviction works in chapters, so the two have to
     * be reconciled through the timeline rather than guessed at from a duration.
     */
    fun bookmarkedChapters(
        bookmarkTimesMs: List<Long>,
        locate: (Long) -> Pair<Int, Long>?,
    ): Set<Int> = bookmarkTimesMs.mapNotNull { locate(it)?.first }.toSet()
}
