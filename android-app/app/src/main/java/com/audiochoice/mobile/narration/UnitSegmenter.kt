package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.NarrationUnit
import com.audiochoice.mobile.data.SourceRange
import java.text.BreakIterator
import java.util.Locale

/**
 * Cuts prose into the spans that will be handed to a voice one at a time.
 *
 * Three levels, applied only when the level above produces something too long for
 * the engine: sentences, then clauses, then words. Sentences are the unit a
 * listener perceives, so splitting mid-sentence is a last resort rather than a
 * convenience.
 *
 * Every unit records the offsets it came from and the characters at those offsets,
 * and those two must always agree. That agreement is the reason the feature can
 * highlight the sentence being spoken and remove the passage a filter covers: the
 * unit *indexes* Book_Text rather than holding a rewritten copy of it. Anything
 * that changes what is spoken -- a pronunciation rule, a filtered range -- is applied
 * downstream, so it can never move an offset.
 */
object UnitSegmenter {

    /**
     * Sentence segmentation uses the platform's locale-aware iterator rather than
     * punctuation matching, because "Mr. Darcy" and "1.5 miles" are not two
     * sentences and a regex will always think they are.
     */
    fun segment(
        bookText: String,
        chapterRange: SourceRange,
        nonProseRanges: List<SourceRange>,
        limit: Int,
        language: String?,
    ): List<NarrationUnit> {
        val prose = subtract(chapterRange, nonProseRanges)
        if (prose.isEmpty()) return emptyList()

        val iterator = BreakIterator.getSentenceInstance(locale(language))
        val units = mutableListOf<NarrationUnit>()

        prose.forEach { range ->
            // Segmenting each prose sub-range separately is what makes an
            // overlapping unit unconstructible rather than merely unlikely: a unit
            // can only be carved out of text that is already known to be prose.
            val slice = bookText.substring(range.start, range.end)
            sentenceBounds(slice, iterator).forEach { (from, to) ->
                splitToLimit(
                    bookText = bookText,
                    start = range.start + from,
                    end = range.start + to,
                    limit = limit,
                ).forEach { unit -> units += unit }
            }
        }

        return units
    }

    /**
     * Sentence bounds, with the iterator's false breaks merged away.
     *
     * Two reasons this is not just the iterator's output. Each unit becomes its own
     * synthesis request, so a break after "Mr." puts an audible pause in the middle
     * of a sentence -- the engine ends an utterance there and starts another.
     *
     * And the iterator is not the same implementation everywhere: the JDK's
     * `BreakIterator` has no abbreviation suppression while Android's is backed by
     * ICU, so relying on its raw boundaries would mean a plan built on a device did
     * not match one built in a test. Deciding the merges here makes segmentation
     * behave the same on both.
     */
    internal fun sentenceBounds(text: String, iterator: BreakIterator): List<Pair<Int, Int>> {
        iterator.setText(text)
        val boundaries = mutableListOf(iterator.first())
        var next = iterator.next()
        while (next != BreakIterator.DONE) {
            boundaries += next
            next = iterator.next()
        }

        val bounds = mutableListOf<Pair<Int, Int>>()
        var start = boundaries.firstOrNull() ?: return emptyList()
        for (index in 1 until boundaries.size) {
            val end = boundaries[index]
            val isLast = index == boundaries.lastIndex
            if (!isLast && isFalseBoundary(text, start, end)) continue
            bounds += start to end
            start = end
        }
        return bounds
    }

    /**
     * Whether a boundary falls inside a sentence rather than at the end of one.
     *
     * Covers the three cases that actually occur in books: a title or honorific, an
     * initial in a name, and a decimal number.
     */
    private fun isFalseBoundary(text: String, start: Int, end: Int): Boolean {
        var cursor = end - 1
        while (cursor >= start && text[cursor].isWhitespace()) cursor--
        if (cursor < start || text[cursor] != '.') return false

        var tokenEnd = cursor
        var tokenStart = tokenEnd
        while (tokenStart > start && (text[tokenStart - 1].isLetterOrDigit())) tokenStart--
        val token = text.substring(tokenStart, tokenEnd)
        if (token.isEmpty()) return false

        // "Mr.", "Dr.", "St." and friends.
        if (token.lowercase(Locale.US) in ABBREVIATIONS) return true

        // An initial: "J. R. R. Tolkien".
        if (token.length == 1 && token[0].isLetter() && token[0].isUpperCase()) return true

        // A decimal: only when a digit follows, so "chapter 12." still ends a sentence.
        if (token.all { it.isDigit() }) {
            var after = end
            while (after < text.length && text[after].isWhitespace()) after++
            return after < text.length && text[after].isDigit()
        }

        return false
    }

    /**
     * The prose of a chapter: its range with every non-prose region removed.
     *
     * Non-prose ranges arrive already merged, so one ordered pass is enough.
     */
    internal fun subtract(range: SourceRange, nonProse: List<SourceRange>): List<SourceRange> {
        var cursor = range.start
        val result = mutableListOf<SourceRange>()
        nonProse.forEach { blocked ->
            if (blocked.end <= cursor) return@forEach
            if (blocked.start >= range.end) return@forEach
            if (blocked.start > cursor) result += SourceRange(cursor, minOf(blocked.start, range.end))
            cursor = maxOf(cursor, blocked.end)
            if (cursor >= range.end) return result.filterNot { it.isEmpty }
        }
        if (cursor < range.end) result += SourceRange(cursor, range.end)
        return result.filterNot { it.isEmpty }
    }

    /**
     * Bring one span within the engine's input limit, splitting as little as
     * possible.
     */
    private fun splitToLimit(
        bookText: String,
        start: Int,
        end: Int,
        limit: Int,
    ): List<NarrationUnit> {
        val trimmed = trim(bookText, start, end) ?: return emptyList()
        if (trimmed.length <= limit) {
            return listOfNotNull(unit(bookText, trimmed))
        }

        // Level two: clause boundaries. Packed greedily, so a long sentence becomes
        // as few units as the limit allows rather than one unit per comma.
        val clauses = packToLimit(clauseBoundaries(bookText, trimmed), limit)

        return clauses.flatMap { clause ->
            if (clause.length <= limit) {
                listOfNotNull(unit(bookText, clause))
            } else {
                // Level three: the last word boundary at or before the limit. Only
                // reached by a clause with no internal punctuation, which is rare
                // and always the engine's constraint rather than the author's.
                splitAtWords(bookText, clause, limit).mapNotNull { unit(bookText, it) }
            }
        }
    }

    /**
     * Offsets of clause starts inside a span: after a comma, semicolon, colon, en
     * dash or em dash that is followed by whitespace.
     *
     * The "followed by whitespace" test is what keeps a decimal, a time or a
     * hyphenated compound from being treated as a clause break.
     */
    private fun clauseBoundaries(bookText: String, range: SourceRange): List<SourceRange> {
        val starts = mutableListOf(range.start)
        var index = range.start
        while (index < range.end - 1) {
            if (bookText[index] in CLAUSE_PUNCTUATION && bookText[index + 1].isWhitespace()) {
                var next = index + 1
                while (next < range.end && bookText[next].isWhitespace()) next++
                if (next < range.end) starts += next
                index = next
            } else {
                index++
            }
        }
        return starts.mapIndexed { position, start ->
            SourceRange(start, starts.getOrNull(position + 1) ?: range.end)
        }.filterNot { it.isEmpty }
    }

    /** Merge adjacent spans while the result stays within [limit]. */
    private fun packToLimit(spans: List<SourceRange>, limit: Int): List<SourceRange> {
        val packed = mutableListOf<SourceRange>()
        spans.forEach { span ->
            val last = packed.lastOrNull()
            if (last != null && span.end - last.start <= limit) {
                packed[packed.lastIndex] = SourceRange(last.start, span.end)
            } else {
                packed += span
            }
        }
        return packed
    }

    private fun splitAtWords(bookText: String, range: SourceRange, limit: Int): List<SourceRange> {
        val pieces = mutableListOf<SourceRange>()
        var cursor = range.start
        while (cursor < range.end) {
            val remaining = range.end - cursor
            if (remaining <= limit) {
                pieces += SourceRange(cursor, range.end)
                break
            }
            val hardEnd = cursor + limit
            var cut = hardEnd
            while (cut > cursor && !bookText[cut].isWhitespace()) cut--
            // A single token longer than the limit has no word boundary to use, so
            // it is cut mid-word. Better a mispronounced long token than a request
            // the engine rejects and a chapter that never renders.
            if (cut <= cursor) cut = hardEnd
            pieces += SourceRange(cursor, cut)
            cursor = cut
            while (cursor < range.end && bookText[cursor].isWhitespace()) cursor++
        }
        return pieces.filterNot { it.isEmpty }
    }

    /**
     * Narrow a span to its first and last non-whitespace character.
     *
     * Done by moving the offsets rather than by trimming a copied string, so the
     * recorded offsets still address exactly the recorded characters.
     */
    private fun trim(bookText: String, start: Int, end: Int): SourceRange? {
        var from = start
        var to = end
        while (from < to && bookText[from].isWhitespace()) from++
        while (to > from && bookText[to - 1].isWhitespace()) to--
        return if (to > from) SourceRange(from, to) else null
    }

    /**
     * A unit, or nothing when the span holds no letter and no digit.
     *
     * A span of punctuation and dividers -- a scene break of asterisks, a stray
     * bullet -- has nothing to say, and asking an engine to speak it produces either
     * silence or a spoken symbol name.
     */
    private fun unit(bookText: String, range: SourceRange): NarrationUnit? {
        val text = bookText.substring(range.start, range.end)
        if (text.none { it.isLetterOrDigit() }) return null
        return NarrationUnit(range.start, range.end, text)
    }

    private fun locale(language: String?): Locale {
        val tag = language?.trim()?.takeIf { it.isNotEmpty() } ?: return Locale.US
        return runCatching { Locale.forLanguageTag(tag) }
            .getOrNull()
            ?.takeIf { it.language.isNotEmpty() }
            ?: Locale.US
    }

    private val CLAUSE_PUNCTUATION = charArrayOf(',', ';', ':', '\u2013', '\u2014')

    /**
     * Abbreviations that end with a period without ending a sentence.
     *
     * Deliberately short and conservative. A word wrongly listed here joins two real
     * sentences into one unit, which is harmless -- it is still under the input limit
     * and still one continuous utterance. A word wrongly omitted puts a pause in the
     * middle of a sentence, which a listener hears. So the list errs toward
     * including the honorifics and units that genuinely appear mid-sentence in prose.
     */
    private val ABBREVIATIONS = setOf(
        "mr", "mrs", "ms", "mx", "dr", "prof", "rev", "fr", "hon", "sr", "jr",
        "st", "mt", "ft", "capt", "gen", "col", "sgt", "lt", "cmdr", "adm", "maj",
        "gov", "sen", "rep", "pres", "supt", "insp", "det",
        "inc", "ltd", "co", "corp", "dept", "est", "univ",
        "vs", "etc", "al", "cf", "ca", "approx", "no", "vol", "pp", "ed", "eds",
        "fig", "figs", "ch", "chap", "sec", "para", "trans", "orig",
        "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sept", "sep", "oct", "nov", "dec",
        "mon", "tue", "tues", "wed", "thu", "thur", "thurs", "fri", "sat", "sun",
        "am", "pm", "a", "p",
    )
}

/**
 * How much text one synthesis request may carry.
 *
 * Capped at a thousand characters independently of what the platform reports, so
 * the plan does not depend on the voice that happens to be selected. A voice with a
 * lower limit is handled at render time by splitting a unit's spoken text, which
 * changes what is sent without moving a single offset.
 */
object SynthesisInputLimit {
    const val CEILING = 1_000

    /** A floor, in case a platform reports something unusable. */
    const val FLOOR = 40

    fun resolve(platformMaximum: Int): Int = when {
        platformMaximum <= 0 -> CEILING
        else -> platformMaximum.coerceIn(FLOOR, CEILING)
    }
}
