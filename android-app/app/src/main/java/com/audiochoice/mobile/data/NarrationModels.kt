package com.audiochoice.mobile.data

import kotlinx.serialization.Serializable

/**
 * Device-only narration models.
 *
 * None of these cross the wire, which is why they live here rather than in
 * `android-contract`: putting them in the shared contract would imply a
 * compatibility obligation with the backend that does not exist. The types that
 * do cross the wire reuse `BookFingerprint` and `ScanEvent` from the contract
 * module, because a text scan returns the same `ScanEvent` shape an audio scan
 * returns.
 *
 * Every offset in this file indexes Book_Text, the flat string narration
 * extraction produces. Book_Text is the single coordinate space for the feature:
 * the plan, the filter events, the reader and the timeline all speak it.
 */

/**
 * A half-open pair of character offsets into Book_Text, `[start, end)`.
 *
 * The same coordinate space `ReaderParagraph` and `ReaderTimingRange` already
 * use, so a range produced by extraction can be handed to the reader without
 * translation.
 */
@Serializable
data class SourceRange(val start: Int, val end: Int) {
    val length: Int get() = end - start
    val isEmpty: Boolean get() = end <= start

    fun overlaps(other: SourceRange): Boolean = start < other.end && other.start < end

    fun contains(offset: Int): Boolean = offset >= start && offset < end
}

/**
 * Merge overlapping and touching ranges into an ordered, disjoint list.
 *
 * Touching counts as overlapping: a range ending exactly where the next begins
 * describes one continuous span, and treating them separately would leave a
 * zero-width seam that later arithmetic has to keep re-deciding about.
 */
fun List<SourceRange>.mergedRanges(): List<SourceRange> {
    if (size <= 1) return filterNot { it.isEmpty }
    val sorted = filterNot { it.isEmpty }.sortedBy { it.start }
    val merged = mutableListOf<SourceRange>()
    sorted.forEach { range ->
        val last = merged.lastOrNull()
        if (last != null && range.start <= last.end) {
            if (range.end > last.end) merged[merged.lastIndex] = last.copy(end = range.end)
        } else {
            merged += range
        }
    }
    return merged
}

/** How a [NarrationUnit] is spoken. */
@Serializable
enum class VoiceKind {
    /** Android `TextToSpeech`. Free, offline, always available, the fallback. */
    SYSTEM,

    /** On-device neural model. Free, offline, offered only where fast enough. */
    LOCAL_NEURAL,

    /** Server-side synthesis. Requires an active premium entitlement. */
    PREMIUM,
}

/** The voice recorded against one narrated book. */
@Serializable
data class SelectedVoice(val kind: VoiceKind, val voiceID: String)

/**
 * One sentence-scale span of Book_Text queued for synthesis.
 *
 * [sourceCharacters] always equals `Book_Text.substring(startCharacter, endCharacter)`.
 * That invariant is the whole point of the type: a unit *indexes* Book_Text and
 * never rewrites it, which is what lets the reader highlight the passage the
 * listener is hearing. Pronunciation rules and filtered-range removal change what
 * is spoken, and they are applied downstream, so they never move an offset.
 */
@Serializable
data class NarrationUnit(
    val startCharacter: Int,
    val endCharacter: Int,
    val sourceCharacters: String,
) {
    val length: Int get() = endCharacter - startCharacter
}

/**
 * An ordered division of Book_Text. Chapter ranges are contiguous and together
 * cover every offset, so no text is orphaned between chapters even when the
 * navigation document skips it.
 *
 * [units] may be empty: a chapter of pure front matter, or one whose every unit
 * was filtered out, has nothing to synthesise and is treated as already rendered.
 */
@Serializable
data class NarrationChapter(
    val index: Int,
    val title: String,
    val startCharacter: Int,
    val endCharacter: Int,
    val units: List<NarrationUnit> = emptyList(),
) {
    val requiresRendering: Boolean get() = units.isNotEmpty()
}

/**
 * Everything a [NarrationPlan] depends on.
 *
 * Recorded with the plan so a stale plan is detected rather than reinterpreted.
 * A [bookTextHash] change means every offset in the plan refers to a coordinate
 * space that no longer exists, which is a different and worse failure than a
 * [planVersion] change, where the offsets are still valid and only the
 * segmentation rules moved. The store treats them differently for that reason.
 */
@Serializable
data class PlanInputs(
    val sourceSha256: String,
    val bookTextHash: String,
    val extractionVersion: Int,
    val planVersion: Int,
    val synthesisInputLimit: Int,
    val enabledEventKeys: List<String> = emptyList(),
    val pronunciationRuleFingerprint: String = "",
)

/**
 * The ordered chapters and units for one narrated book.
 *
 * Construction is a pure function of its [inputs], so the same inputs produce an
 * equal plan on every run. Nothing time-dependent, randomly ordered or
 * hash-set-iterated may enter it, because a plan that differs between runs would
 * silently invalidate rendered audio.
 */
@Serializable
data class NarrationPlan(
    val planVersion: Int,
    val inputs: PlanInputs,
    val chapterDerivationFellBackToSpine: Boolean = false,
    val chapters: List<NarrationChapter> = emptyList(),
) {
    val totalUnits: Int get() = chapters.sumOf { it.units.size }

    val totalSpokenCharacters: Int
        get() = chapters.sumOf { chapter -> chapter.units.sumOf { it.length } }

    companion object {
        /**
         * Increment whenever chapter derivation, non-prose classification or unit
         * segmentation changes. Serves the purpose `READER_ALIGNMENT_VERSION`
         * serves for reader alignment: a persisted plan from a different version
         * is discarded and rebuilt rather than trusted.
         */
        const val PLAN_VERSION = 1
    }
}

/** Where one chapter stands in the render pipeline. */
@Serializable
enum class RenderState {
    NOT_RENDERED,
    RENDERING,
    RENDERED,

    /**
     * Retries exhausted. Deliberately distinct from [NOT_RENDERED] so the
     * scheduler steps past it instead of retrying forever; it returns to
     * [NOT_RENDERED] only when the listener asks for another attempt.
     */
    RENDER_FAILED,
}

/**
 * Per-chapter render state for one book.
 *
 * Parallel lists rather than a list of records because the state is rewritten on
 * every chapter completion and the flat shape keeps that write small. All lists
 * are the same length as the plan's chapter list.
 */
@Serializable
data class RenderQueue(
    val states: List<RenderState> = emptyList(),
    val chapterDurationsMs: List<Long> = emptyList(),
    /** Units dropped entirely because a filter covered them in full. */
    val omittedUnitCounts: List<Int> = emptyList(),
    /** Units where a filter removed part of the text but some remained. */
    val partiallyRemovedUnitCounts: List<Int> = emptyList(),
    val failureReasons: Map<Int, String> = emptyMap(),
) {
    val renderedCount: Int get() = states.count { it == RenderState.RENDERED }
    val failedCount: Int get() = states.count { it == RenderState.RENDER_FAILED }
    val chapterCount: Int get() = states.size

    /** Narration_Duration: rendered chapters only, so it grows as rendering proceeds. */
    val renderedDurationMs: Long
        get() = states.indices.sumOf { index ->
            if (states[index] == RenderState.RENDERED) {
                chapterDurationsMs.getOrElse(index) { 0L }
            } else {
                0L
            }
        }

    val isFullyRendered: Boolean
        get() = states.isNotEmpty() && states.all { it == RenderState.RENDERED }

    /**
     * The contiguous run of rendered chapters after [chapterIndex].
     *
     * Contiguous, not total: a gap ahead of the listener is a wall they will hit,
     * so counting past it would satisfy the render-ahead window on paper and
     * stall playback in practice.
     */
    fun renderedRunAfter(chapterIndex: Int): Int {
        var count = 0
        var index = chapterIndex + 1
        while (index < states.size && states[index] == RenderState.RENDERED) {
            count++
            index++
        }
        return count
    }

    fun withState(chapterIndex: Int, state: RenderState): RenderQueue =
        copy(states = states.toMutableList().also { it[chapterIndex] = state })

    companion object {
        /**
         * A chapter with no units has nothing to produce, so it starts rendered
         * with a zero duration rather than sitting in the queue forever.
         */
        fun forPlan(plan: NarrationPlan): RenderQueue = RenderQueue(
            states = plan.chapters.map {
                if (it.requiresRendering) RenderState.NOT_RENDERED else RenderState.RENDERED
            },
            chapterDurationsMs = plan.chapters.map { 0L },
            omittedUnitCounts = plan.chapters.map { 0 },
            partiallyRemovedUnitCounts = plan.chapters.map { 0 },
        )
    }
}

/**
 * A listener's pronunciation correction, applied to spoken text only.
 *
 * Scope is carried by where the rule is persisted rather than by a field: rules
 * for one book live under that book's key, account-wide rules under the account
 * key. The resolver receives the two lists separately, which is also how the
 * precedence rule is expressed -- book rules before account rules, and within one
 * scope, earlier-recorded before later.
 */
@Serializable
data class PronunciationRule(
    val writtenForm: String,
    val replacementForm: String,
    val order: Int,
) {
    companion object {
        const val MAXIMUM_FORM_LENGTH = 100
        const val MAXIMUM_RULES_PER_SCOPE = 200
    }
}

/**
 * Per-book narration flags, stored as one JSON value rather than a key each.
 *
 * They are read together and written together, and the existing preferences
 * document is rewritten whole on every edit, so one value means one rewrite
 * instead of six.
 */
@Serializable
data class NarrationFlags(
    val fullBookRenderRequested: Boolean = false,
    val renderingPausedByListener: Boolean = false,
    val audioEvictionEnabled: Boolean = false,
    /** Listener chose to narrate without filter results after a scan failure. */
    val continuedWithoutFilterResults: Boolean = false,
)
