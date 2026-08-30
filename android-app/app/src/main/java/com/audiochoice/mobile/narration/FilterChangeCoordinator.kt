package com.audiochoice.mobile.narration

import com.audiochoice.contracts.ScanEvent
import com.audiochoice.mobile.data.NarrationChapter
import com.audiochoice.mobile.data.RenderState
import com.audiochoice.mobile.data.VoiceKind
import com.audiochoice.mobile.player.PlaybackFilterPredicate

/** The filter choices that decide which events are switched on. */
data class FilterChoices(
    val disabledCategoryIDs: Set<String> = emptySet(),
    val disabledGroupIDs: Set<String> = emptySet(),
    val disabledEventKeys: Set<String> = emptySet(),
    val disabledAggregateKeys: Set<String> = emptySet(),
)

/** What changing a filter costs, presented before anything is discarded. */
sealed interface FilterChangeImpact {

    /**
     * No rendered audio is affected, so the choice is written immediately with no confirmation.
     *
     * The common case by far: most filter changes touch categories a book never triggered.
     * Asking for confirmation here would train listeners to dismiss the dialogue that matters.
     */
    data object None : FilterChangeImpact

    data class Rerender(
        val affectedChapterIndices: List<Int>,
        val estimatedMinutes: Int,
        /**
         * How many affected chapters would be sent to the premium voice again.
         *
         * Named separately because it is the one part of a re-render with a cost beyond
         * waiting, and a listener choosing to toggle a filter deserves to know that before
         * they do it rather than after.
         */
        val chaptersResynthesizedByPremiumVoice: Int,
    ) : FilterChangeImpact {
        val chapterCount: Int get() = affectedChapterIndices.size
    }
}

/**
 * Works out which chapters a filter change invalidates, and what to tell the listener.
 *
 * Pure. The decision this makes -- discard hours of audio or not -- is one that has to be
 * inspectable without a device, and reversible without a render.
 */
object FilterChangeCoordinator {

    /**
     * Which rendered or rendering chapters overlap an event whose enabled state changed.
     *
     * Enabled state is recomputed through the unmodified [PlaybackFilterPredicate], so a
     * narrated book and an audiobook agree about what a choice means. Only the events whose
     * state actually *changed* matter: a listener switching a category off and on again leaves
     * nothing to do, and comparing before against after is what notices that.
     *
     * A chapter with no audio is not affected, whatever it overlaps. Nothing has been
     * synthesised for it, so there is nothing to discard and nothing to warn about.
     */
    fun affectedChapters(
        chapters: List<NarrationChapter>,
        states: List<RenderState>,
        events: List<ScanEvent>,
        before: FilterChoices,
        after: FilterChoices,
    ): List<Int> {
        val changed = changedEvents(events, before, after)
        if (changed.isEmpty()) return emptyList()

        return chapters.indices.filter { index ->
            val state = states.getOrNull(index)
            if (state != RenderState.RENDERED && state != RenderState.RENDERING) return@filter false
            val chapter = chapters[index]
            changed.any { event ->
                // Half-open ranges throughout, so an event ending exactly where a chapter
                // begins belongs to the earlier chapter and is not counted twice.
                event.startTime < chapter.endCharacter.toDouble() &&
                    event.endTime > chapter.startCharacter.toDouble()
            }
        }
    }

    /** Events whose enabled state differs between the two sets of choices. */
    fun changedEvents(
        events: List<ScanEvent>,
        before: FilterChoices,
        after: FilterChoices,
    ): List<ScanEvent> = events.filter { event ->
        isEnabled(event, before) != isEnabled(event, after)
    }

    private fun isEnabled(event: ScanEvent, choices: FilterChoices): Boolean =
        PlaybackFilterPredicate.isEnabled(
            event = event,
            disabledCategoryIDs = choices.disabledCategoryIDs,
            disabledGroupIDs = choices.disabledGroupIDs,
            disabledEventKeys = choices.disabledEventKeys,
            disabledAggregateKeys = choices.disabledAggregateKeys,
        )

    /** What to present, or [FilterChangeImpact.None] when there is nothing to confirm. */
    fun impactOf(
        chapters: List<NarrationChapter>,
        states: List<RenderState>,
        events: List<ScanEvent>,
        before: FilterChoices,
        after: FilterChoices,
        voiceKind: VoiceKind,
    ): FilterChangeImpact {
        val affected = affectedChapters(chapters, states, events, before, after)
        if (affected.isEmpty()) return FilterChangeImpact.None

        return FilterChangeImpact.Rerender(
            affectedChapterIndices = affected,
            estimatedMinutes = estimatedMinutes(affected.map { chapters[it] }, voiceKind),
            chaptersResynthesizedByPremiumVoice =
                if (voiceKind == VoiceKind.PREMIUM) affected.size else 0,
        )
    }

    /**
     * A whole-minute estimate of how long re-rendering will take.
     *
     * Derived from the same characters-per-second figures the storage estimate uses, so the two
     * numbers a listener sees cannot contradict each other. Rounded up and never zero: "this
     * will take 0 minutes" followed by a wait is worse than saying one minute.
     */
    fun estimatedMinutes(chapters: List<NarrationChapter>, voiceKind: VoiceKind): Int {
        val characters = chapters.sumOf { NarrationStorage.spokenCharacters(it) }
        if (characters == 0) return 0
        // Synthesis is faster than real time, which is why this is not simply the audio's own
        // duration. The multiple is deliberately conservative: an estimate that runs under is
        // far more annoying than one that runs over.
        val secondsOfAudio = characters / NarrationStorage.charactersPerSecond(voiceKind)
        val renderSeconds = secondsOfAudio / SYNTHESIS_SPEED_MULTIPLE
        return maxOf(1, Math.ceil(renderSeconds / 60.0).toInt())
    }

    /**
     * How much faster than real time synthesis runs.
     *
     * An estimate, and labelled as one. Four is pessimistic for a server voice and about right
     * for an on-device one, which is the direction to err in.
     */
    const val SYNTHESIS_SPEED_MULTIPLE = 4.0

    /** How long identification may take before the listener is left waiting. */
    const val IDENTIFICATION_DEADLINE_MS = 2_000L

    /** How long stopping a mid-render chapter may take. */
    const val STOP_DEADLINE_MS = 5_000L

    /**
     * Whether playback can continue uninterrupted through a re-render.
     *
     * True when the chapter being listened to is not one of the affected ones. Then only a
     * later playlist item changes, so the affected item can be replaced rather than the
     * playlist reset -- which is the difference between a listener noticing nothing and their
     * audio stopping.
     */
    fun playbackCanContinue(
        affectedChapterIndices: List<Int>,
        currentChapterIndex: Int,
    ): Boolean = currentChapterIndex !in affectedChapterIndices

    /**
     * The order to re-render in: the listener's own chapter first, then plan order.
     *
     * Whoever is waiting is waiting for one chapter, and it is the one they are in. Rendering
     * the book from the start would make them wait through every earlier chapter first.
     */
    fun renderOrder(
        affectedChapterIndices: List<Int>,
        currentChapterIndex: Int,
    ): List<Int> {
        val sorted = affectedChapterIndices.sorted()
        if (currentChapterIndex !in sorted) return sorted
        return listOf(currentChapterIndex) + sorted.filter { it != currentChapterIndex }
    }

    /**
     * Whether the position can be restored yet.
     *
     * Only once every affected chapter *before* the target offset has been rendered, because
     * until then the earlier chapters have no durations and Book_Time for the target does not
     * exist yet. This is also why the offset is recorded in characters at the moment of
     * confirmation: re-rendering changes chapter durations, so the Book_Time the listener was
     * at no longer denotes the same words.
     */
    fun canRestorePosition(
        affectedChapterIndices: List<Int>,
        states: List<RenderState>,
        targetChapterIndex: Int,
    ): Boolean = affectedChapterIndices
        .filter { it <= targetChapterIndex }
        .all { states.getOrNull(it) == RenderState.RENDERED }
}
