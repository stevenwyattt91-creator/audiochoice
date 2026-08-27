package com.audiochoice.mobile.reader

/** A half-open character range in the flat EPUB text that a filter covers. */
data class ReaderMask(val start: Int, val end: Int)

/** One paragraph prepared for display, with filtered text physically removed. */
data class ReaderDisplayParagraph(
    val paragraph: ReaderParagraph,
    /** What is actually rendered. Filtered characters are absent, not styled over. */
    val displayText: String,
    /** How many separate filtered passages were removed from this paragraph. */
    val removedPassages: Int,
) {
    val hasRemovedText: Boolean get() = removedPassages > 0
}

/** Merges overlapping and touching ranges so removal never double-counts. */
fun List<ReaderMask>.merged(): List<ReaderMask> =
    sortedBy { it.start }.fold(mutableListOf()) { result, next ->
        val previous = result.lastOrNull()
        if (previous != null && next.start <= previous.end) {
            result[result.lastIndex] = ReaderMask(previous.start, maxOf(previous.end, next.end))
        } else {
            result += next
        }
        result
    }

/**
 * Marker left where text was removed, so a sentence that suddenly changes
 * direction reads as a deliberate edit rather than a rendering bug.
 */
const val READER_REMOVAL_MARKER = "…"

/**
 * Builds display text for every paragraph with filtered passages **removed**.
 *
 * Removal rather than styling is deliberate. The previous reader painted a black
 * `SpanStyle` over filtered ranges, which left the characters present in the
 * `AnnotatedString` -- so a screen reader, the semantics tree, or any
 * text-extraction path surfaced exactly the content the audio path skips.
 *
 * [masks] must already be [merged]. Each paragraph keeps its original
 * [ReaderParagraph] so audio-follow can still map display back to source offsets.
 */
fun readerDisplayParagraphs(
    paragraphs: List<ReaderParagraph>,
    masks: List<ReaderMask>,
): List<ReaderDisplayParagraph> {
    if (masks.isEmpty()) {
        return paragraphs.map { ReaderDisplayParagraph(it, it.text, 0) }
    }
    return paragraphs.map { paragraph -> maskParagraph(paragraph, masks) }
}

private fun maskParagraph(
    paragraph: ReaderParagraph,
    masks: List<ReaderMask>,
): ReaderDisplayParagraph {
    var maskIndex = firstMaskEndingAfter(masks, paragraph.startCharacter)
    if (maskIndex >= masks.size || masks[maskIndex].start >= paragraph.endCharacter) {
        return ReaderDisplayParagraph(paragraph, paragraph.text, 0)
    }

    val builder = StringBuilder(paragraph.text.length)
    var cursor = paragraph.startCharacter
    var removed = 0
    while (maskIndex < masks.size && masks[maskIndex].start < paragraph.endCharacter) {
        val mask = masks[maskIndex]
        val overlapStart = maxOf(mask.start, paragraph.startCharacter)
        val overlapEnd = minOf(mask.end, paragraph.endCharacter)
        if (overlapEnd > overlapStart) {
            if (overlapStart > cursor) {
                builder.append(
                    paragraph.text,
                    cursor - paragraph.startCharacter,
                    overlapStart - paragraph.startCharacter,
                )
            }
            builder.append(READER_REMOVAL_MARKER)
            removed += 1
            cursor = maxOf(cursor, overlapEnd)
        }
        maskIndex += 1
    }
    if (cursor < paragraph.endCharacter) {
        builder.append(
            paragraph.text,
            cursor - paragraph.startCharacter,
            paragraph.endCharacter - paragraph.startCharacter,
        )
    }
    // Removing mid-sentence text can strand doubled spaces around the marker.
    val display = builder.toString().replace(WHITESPACE_RUN, " ").trim()
    return ReaderDisplayParagraph(paragraph, display, removed)
}

/** Binary search for the first mask that could overlap [character]. */
private fun firstMaskEndingAfter(masks: List<ReaderMask>, character: Int): Int {
    var low = 0
    var high = masks.size
    while (low < high) {
        val middle = (low + high) / 2
        if (masks[middle].end <= character) low = middle + 1 else high = middle
    }
    return low
}

private val WHITESPACE_RUN = Regex("\\s{2,}")
