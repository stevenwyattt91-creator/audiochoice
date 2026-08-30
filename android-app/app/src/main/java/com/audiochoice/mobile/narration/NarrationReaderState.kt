package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.ReaderTimingRange
import com.audiochoice.mobile.data.SourceRange
import com.audiochoice.mobile.reader.ReaderDisplayParagraph
import com.audiochoice.mobile.reader.ReaderMask
import com.audiochoice.mobile.reader.ReaderParagraph
import com.audiochoice.mobile.reader.ReaderParagraphParser
import com.audiochoice.mobile.reader.readerCharacterForTime
import com.audiochoice.mobile.reader.readerDisplayParagraphs
import com.audiochoice.mobile.reader.readerTimeForCharacter

/**
 * Everything the read-along needs for a narrated book, derived rather than rendered.
 *
 * The reader components themselves need no change. Paragraphs come from the same parser,
 * masking from the same function, and the two time-to-character conversions from the
 * same pair the imported-audiobook path uses. That is not a coincidence: narration
 * produces timings in the coordinate space those functions already speak, so the work
 * here is deciding *what* to show rather than teaching the reader anything new.
 *
 * Keeping the decisions in a pure function means the interesting cases -- a paragraph
 * removed entirely by a filter, a tap on text that has no audio yet, a gap in coverage --
 * are unit tests rather than things someone has to reproduce by scrolling a book on a
 * device.
 *
 * One genuine difference from an imported audiobook is worth stating. Reader alignment
 * for an audiobook is deliberately sparse: the server skips any transcript segment it
 * cannot confidently anchor, so gaps are normal and both conversions return null across
 * them. Narration has no anchoring problem, because the offsets were known before the
 * audio existed. Coverage is therefore total over rendered prose, and a gap now means
 * something specific: non-prose, or a chapter that has not been produced yet.
 */
object NarrationReaderState {

    /**
     * Derive the reader's view of a narrated book.
     *
     * [bookTimeSeconds] is a position in the book, not in a chapter's file, so the caller
     * hands over what the player already reports rather than doing its own arithmetic.
     */
    fun derive(
        bookText: String,
        filteredRanges: List<ReaderMask>,
        narrationTimingRanges: List<ReaderTimingRange>,
        nonProseRanges: List<SourceRange> = emptyList(),
        bookTimeSeconds: Double? = null,
        previousHighlightIndex: Int? = null,
        paragraphs: List<ReaderParagraph> = ReaderParagraphParser.parse(bookText),
    ): ReaderView {
        val display = readerDisplayParagraphs(paragraphs, filteredRanges)

        // A paragraph a filter covered entirely renders not at all. Masking replaces
        // removed text with an ellipsis marker, so such a paragraph is not blank -- it is a
        // lone marker, and leaving that row in place would point at exactly the passage
        // removing it was meant to hide.
        //
        // Tested both ways: fully covered by construction, and with nothing left that
        // could be read aloud, which also catches a paragraph covered by several ranges
        // that were not merged.
        val visible = display.filterIndexed { index, paragraph ->
            !isFullyMasked(paragraphs[index], filteredRanges) &&
                paragraph.displayText.any { it.isLetterOrDigit() }
        }

        val character = bookTimeSeconds?.let { readerCharacterForTime(narrationTimingRanges, it) }

        val highlight = when {
            character == null -> previousHighlightIndex
            else -> paragraphs.indexOfFirst { character >= it.startCharacter && character < it.endCharacter }
                .takeIf { it >= 0 }
                ?.takeIf { index -> !isNonProse(paragraphs[index], nonProseRanges) }
                ?: previousHighlightIndex
        }

        return ReaderView(
            paragraphs = paragraphs,
            displayParagraphs = display,
            visibleParagraphs = visible,
            highlightedParagraphIndex = highlight,
            highlightedCharacter = character,
            // Only scroll when the highlight actually moved. Re-scrolling on every tick
            // would fight a listener who scrolled back to reread something.
            scrollToHighlight = highlight != null && highlight != previousHighlightIndex,
        )
    }

    /**
     * Where tapping a paragraph should seek to, or null when it should do nothing.
     *
     * Falls forward to the first offset in the paragraph that narration actually covers,
     * because a paragraph can begin with a page number or a footnote marker that was never
     * spoken. Returning null rather than seeking to zero is what keeps a tap on
     * not-yet-rendered text from throwing the listener back to the start of the book.
     */
    fun tapTarget(
        paragraph: ReaderParagraph,
        narrationTimingRanges: List<ReaderTimingRange>,
    ): TapTarget {
        if (narrationTimingRanges.isEmpty()) return TapTarget.NoNarrationYet

        val covered = narrationTimingRanges.firstOrNull { timing ->
            timing.endCharacter > paragraph.startCharacter &&
                timing.startCharacter < paragraph.endCharacter
        } ?: return TapTarget.NoNarrationYet

        val offset = maxOf(paragraph.startCharacter, covered.startCharacter)
        val seconds = readerTimeForCharacter(narrationTimingRanges, offset)
            ?: return TapTarget.NoNarrationYet
        return TapTarget.Seek(seconds)
    }

    private fun isFullyMasked(paragraph: ReaderParagraph, masks: List<ReaderMask>): Boolean =
        masks.any { it.start <= paragraph.startCharacter && it.end >= paragraph.endCharacter }

    private fun isNonProse(paragraph: ReaderParagraph, nonProse: List<SourceRange>): Boolean =
        nonProse.any { it.start <= paragraph.startCharacter && it.end >= paragraph.endCharacter }
}

/** The reader's derived view of a narrated book. */
data class ReaderView(
    val paragraphs: List<ReaderParagraph>,
    val displayParagraphs: List<ReaderDisplayParagraph>,
    /** Paragraphs to actually render; a fully filtered paragraph is absent. */
    val visibleParagraphs: List<ReaderDisplayParagraph>,
    val highlightedParagraphIndex: Int?,
    val highlightedCharacter: Int?,
    val scrollToHighlight: Boolean,
)

sealed interface TapTarget {
    data class Seek(val bookTimeSeconds: Double) : TapTarget

    /**
     * The tapped text has no audio: it is in a chapter that has not been produced, or it
     * is non-prose that was never spoken. The caller leaves the position alone and says so.
     */
    data object NoNarrationYet : TapTarget
}
