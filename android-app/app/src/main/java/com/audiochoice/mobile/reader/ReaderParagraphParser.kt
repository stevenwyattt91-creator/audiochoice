package com.audiochoice.mobile.reader

/**
 * One rendered block of reading text, carrying its position in the flat EPUB
 * string produced by [EpubTextReader.read].
 *
 * [startCharacter] and [endCharacter] are indices into that exact string, which
 * is also the coordinate space the server's reader alignment returns. The
 * invariant `epubText.substring(startCharacter, endCharacter) == text` always
 * holds, so a paragraph can be mapped back to audio timing and to filter mask
 * ranges without any second interpretation of the source.
 */
data class ReaderParagraph(
    val text: String,
    /** Inclusive index into the flat EPUB text. */
    val startCharacter: Int,
    /** Exclusive index into the flat EPUB text. */
    val endCharacter: Int,
)

/**
 * Splits the flat EPUB text into paragraphs for display without altering it.
 *
 * [EpubTextReader.read] must stay byte-for-byte stable because every cached
 * alignment indexes into its output, so this parser only ever *indexes* the
 * string. It never rewrites, re-joins, or normalises it.
 */
object ReaderParagraphParser {

    /**
     * `EpubTextReader` emits one newline per block element and collapses runs of
     * three or more, so a newline run is the paragraph separator.
     */
    fun parse(epubText: String): List<ReaderParagraph> {
        val paragraphs = mutableListOf<ReaderParagraph>()
        val length = epubText.length
        var index = 0
        while (index < length) {
            if (epubText[index] == '\n') {
                index++
                continue
            }
            var start = index
            while (index < length && epubText[index] != '\n') index++
            var end = index
            // Tighten past surrounding spaces so the recorded offsets bound the
            // returned text exactly rather than approximately.
            while (start < end && epubText[start].isWhitespace()) start++
            while (end > start && epubText[end - 1].isWhitespace()) end--
            if (end > start) {
                paragraphs += ReaderParagraph(epubText.substring(start, end), start, end)
            }
        }
        return paragraphs
    }
}

/**
 * Index of the paragraph containing [character], or the nearest preceding one,
 * or -1 when the list is empty.
 *
 * Paragraphs are ordered and non-overlapping, so this binary searches rather
 * than scanning: audio-follow calls it on every position tick.
 */
fun List<ReaderParagraph>.indexOfCharacter(character: Int): Int {
    if (isEmpty()) return -1
    var low = 0
    var high = size - 1
    var best = 0
    while (low <= high) {
        val middle = (low + high) / 2
        val paragraph = this[middle]
        when {
            character < paragraph.startCharacter -> high = middle - 1
            character >= paragraph.endCharacter -> {
                best = middle
                low = middle + 1
            }
            else -> return middle
        }
    }
    return best
}
