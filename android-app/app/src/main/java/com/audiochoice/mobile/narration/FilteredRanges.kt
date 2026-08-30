package com.audiochoice.mobile.narration

import com.audiochoice.contracts.ScanEvent
import com.audiochoice.mobile.data.NarrationUnit
import com.audiochoice.mobile.player.PlaybackFilterPredicate
import com.audiochoice.mobile.reader.ReaderMask
import com.audiochoice.mobile.reader.merged

/**
 * Turns the listener's filter choices into the character ranges narration must not
 * speak.
 *
 * For a narrated book a scan event's `startTime` and `endTime` carry character
 * offsets into Book_Text rather than seconds. That reuse is what buys the whole
 * existing filter stack unchanged -- the same taxonomy builds the control tree, the
 * same predicate decides what is on, the same settings record syncs the choices --
 * and it is also the sharpest edge in the feature, because a consumer that reads
 * those fields as seconds will do something wildly wrong. Every place that reads
 * them for a narrated book goes through this file.
 *
 * Ranges are expressed as [ReaderMask] and merged with the reader's own
 * [merged] rather than with a private merge. That is not laziness: it guarantees
 * the reader and the renderer cannot disagree about what is filtered, which is the
 * difference between a listener seeing text they will not hear and the two matching.
 */
object FilteredRanges {

    /**
     * Merged ranges for the events still switched on.
     *
     * The merge treats touching ranges as one, since a range ending exactly where
     * the next begins describes one continuous passage.
     */
    fun forEnabledEvents(
        events: List<ScanEvent>,
        disabledCategoryIDs: Set<String> = emptySet(),
        disabledGroupIDs: Set<String> = emptySet(),
        disabledEventKeys: Set<String> = emptySet(),
        disabledAggregateKeys: Set<String> = emptySet(),
    ): List<ReaderMask> = events
        .filter { event ->
            PlaybackFilterPredicate.isEnabled(
                event = event,
                disabledCategoryIDs = disabledCategoryIDs,
                disabledGroupIDs = disabledGroupIDs,
                disabledEventKeys = disabledEventKeys,
                disabledAggregateKeys = disabledAggregateKeys,
            )
        }
        .map { ReaderMask(it.startTime.toInt(), it.endTime.toInt()) }
        .merged()

    /**
     * Whether every event's offsets are usable against a book of [bookTextLength].
     *
     * Validated as a batch rather than per event, and deliberately so: an
     * out-of-range offset means the server and the client disagree about the
     * coordinate space, and in that state no event in the set can be trusted. Half
     * a filter is worse than none, because the listener believes filtering is on.
     */
    fun offsetsAreValid(events: List<ScanEvent>, bookTextLength: Int): Boolean =
        events.all { event ->
            val start = event.startTime
            val end = event.endTime
            start >= 0.0 &&
                end > start &&
                end <= bookTextLength.toDouble() &&
                start == kotlin.math.floor(start) &&
                end == kotlin.math.floor(end)
        }
}

/**
 * What a chapter's units become once filtering is applied.
 *
 * Exclusion happens here, before anything is handed to a voice, which is what makes
 * a filtered passage never spoken, never written to the device, and never sent off
 * it. That is stronger than the imported-audiobook path can manage: there the audio
 * already contains the passage and the player seeks past it.
 */
data class ChapterSpeech(
    /** Units to synthesise, in order, with filtered characters already removed. */
    val spoken: List<SpokenUnit>,
    /** Units dropped entirely because a filter covered them. */
    val omittedUnits: Int,
    /** Units that kept some text after a filter removed part of them. */
    val partiallyRemovedUnits: Int,
) {
    /**
     * A chapter with nothing left to say. It writes no audio, records an empty
     * timeline, counts as rendered, and adds nothing to the book's duration.
     */
    val isSilent: Boolean get() = spoken.isEmpty()
}

/**
 * One unit prepared for a voice.
 *
 * [range] is always the whole original unit even when characters were removed from
 * [text]. That is what keeps the reader honest: the timeline entry covers the
 * passage the listener is hearing, and the reader removes the same characters from
 * the display, so the two agree about what is present.
 */
data class SpokenUnit(
    val startCharacter: Int,
    val endCharacter: Int,
    val text: String,
) {
    val range: IntRange get() = startCharacter until endCharacter
}

object SpokenTextBuilder {

    /**
     * Apply merged filtered ranges to one chapter's units.
     *
     * [filtered] must already be merged, which the reader's merge guarantees.
     */
    fun build(units: List<NarrationUnit>, filtered: List<ReaderMask>): ChapterSpeech {
        if (filtered.isEmpty()) {
            // Nothing switched on. Every unit is spoken exactly as planned, with no
            // copying and no rebuilding.
            return ChapterSpeech(
                spoken = units.map { SpokenUnit(it.startCharacter, it.endCharacter, it.sourceCharacters) },
                omittedUnits = 0,
                partiallyRemovedUnits = 0,
            )
        }

        val spoken = mutableListOf<SpokenUnit>()
        var omitted = 0
        var partial = 0

        units.forEach { unit ->
            val overlapping = filtered.filter { it.end > unit.startCharacter && it.start < unit.endCharacter }
            if (overlapping.isEmpty()) {
                spoken += SpokenUnit(unit.startCharacter, unit.endCharacter, unit.sourceCharacters)
                return@forEach
            }

            val kept = keptText(unit, overlapping)
            if (kept == null) {
                omitted++
                return@forEach
            }
            partial++
            spoken += SpokenUnit(unit.startCharacter, unit.endCharacter, kept)
        }

        return ChapterSpeech(spoken = spoken, omittedUnits = omitted, partiallyRemovedUnits = partial)
    }

    /**
     * The characters of [unit] that survive [overlapping], or null when nothing
     * worth speaking remains.
     *
     * A space is inserted at each removal boundary so two clauses that were never
     * adjacent are not spoken as one run-together word.
     *
     * Returning null when the remainder holds no letter or digit is not an
     * optimisation. A unit reduced to a comma and a quotation mark would otherwise
     * be sent to an engine that either says nothing, in which case the timeline
     * entry describes silence, or reads the punctuation aloud.
     */
    private fun keptText(unit: NarrationUnit, overlapping: List<ReaderMask>): String? {
        val builder = StringBuilder(unit.length)
        var cursor = unit.startCharacter
        var removedAnything = false

        overlapping.forEach { mask ->
            val from = maxOf(mask.start, unit.startCharacter)
            val to = minOf(mask.end, unit.endCharacter)
            if (to <= from) return@forEach
            if (from > cursor) {
                builder.append(
                    unit.sourceCharacters,
                    cursor - unit.startCharacter,
                    from - unit.startCharacter,
                )
            }
            removedAnything = true
            if (builder.isNotEmpty() && builder.last() != ' ') builder.append(' ')
            cursor = maxOf(cursor, to)
        }
        if (cursor < unit.endCharacter) {
            builder.append(
                unit.sourceCharacters,
                cursor - unit.startCharacter,
                unit.endCharacter - unit.startCharacter,
            )
        }

        if (!removedAnything) return unit.sourceCharacters
        val kept = builder.toString().replace(WHITESPACE_RUN, " ").trim()
        return kept.takeIf { text -> text.any { it.isLetterOrDigit() } }
    }

    private val WHITESPACE_RUN = Regex("\\s{2,}")
}
