package com.audiochoice.mobile.importing

/**
 * Turns a filename into something presentable when a file carries no usable tags.
 *
 * This is explicitly a last resort. A filename cannot be trusted to name an
 * edition -- it cannot fix a misspelling, and it has no idea whether a recording
 * is dramatized or which part it is. The value here is only that a listener sees
 * "Fourth Wing" rather than "fourth wingggg (3112r)". Deciding *which edition*
 * a file actually is remains the job of tag and catalog matching, and callers
 * must still flag anything derived from a filename as unidentified.
 */
object EditionTitleCleaner {

    /**
     * Bracketed groups holding these words carry real edition meaning and are
     * always kept, even when they also contain digits.
     */
    private val MEANINGFUL_TOKENS = setOf(
        "part", "parts", "of", "book", "vol", "volume", "disc", "cd",
        "unabridged", "abridged", "dramatized", "dramatised", "dramatization",
        "complete", "collection", "edition", "box", "boxed", "set", "narrated",
    )

    /** Encoding and format details, which say nothing about the edition. */
    private val TECHNICAL_TOKENS = setOf(
        "kbps", "kbit", "khz", "hz", "bit", "bits", "kb", "mb", "gb",
        "mp3", "m4a", "m4b", "mp4", "aax", "aaxc", "aac", "flac", "ogg", "opus", "wav", "wma",
        "stereo", "mono", "vbr", "cbr", "abr", "audiobook", "audio", "rip", "retail",
    )

    /**
     * Compact part markers such as "1of2", "Part1" or "Pt.2" tokenize as a single
     * letters-and-digits run, which the noise rules below would otherwise discard.
     * Losing a part number is far worse than keeping a stray group, because
     * edition matching relies on it to tell Part 1 from Part 2.
     */
    private val COMPACT_PART_MARKER = Regex(
        """(\d+\s*of\s*\d+|(?:part|pt|cd|disc|vol|volume|book)\.?\s*\d+)""",
        RegexOption.IGNORE_CASE,
    )

    private val BRACKETED_GROUP = Regex("""[(\[{]([^)\]}]*)[)\]}]""")
    private val LEADING_TRACK_NUMBER = Regex("""^\s*\d{1,3}\s*[-\u2013\u2014.)]\s*""")
    private val LEADING_SEPARATORS = Regex("""^[\s\-\u2013\u2014_.]+""")
    private val TRAILING_SEPARATORS = Regex("""[\s\-\u2013\u2014_.]+$""")
    private val REPEATED_WHITESPACE = Regex("""\s{2,}""")

    /**
     * @return a tidied title, or null when nothing usable survives.
     */
    fun clean(fileName: String): String? {
        val withoutExtension = fileName
            .substringBeforeLast('.', fileName)
            .ifBlank { fileName }

        var text = withoutExtension.replace('_', ' ').replace('\u00A0', ' ')
        // Dots are a separator in "the.hobbit.unabridged" but part of the title in
        // "Vol. 2", so only expand them when the name has no real spaces at all.
        if (!text.contains(' ') && text.contains('.')) {
            text = text.replace('.', ' ')
        }

        text = removeNoiseGroups(text)
        text = LEADING_TRACK_NUMBER.replace(text, "")
        text = LEADING_SEPARATORS.replace(text, "")
        text = TRAILING_SEPARATORS.replace(text, "")
        text = REPEATED_WHITESPACE.replace(text, " ").trim()

        return text.takeIf { it.isNotBlank() }
    }

    private fun removeNoiseGroups(text: String): String =
        BRACKETED_GROUP.replace(text) { match ->
            val tokens = match.groupValues[1]
                .lowercase()
                .split(Regex("[^a-z0-9]+"))
                .filter { it.isNotEmpty() }

            when {
                tokens.isEmpty() -> ""
                // Keep anything that names a part, format or edition.
                tokens.any { it in MEANINGFUL_TOKENS } -> match.value
                COMPACT_PART_MARKER.containsMatchIn(match.groupValues[1]) -> match.value
                tokens.any { it in TECHNICAL_TOKENS } -> ""
                // Codes such as "3112r" or an Audible ASIN mix letters and digits.
                tokens.any { token -> token.any(Char::isDigit) && token.any(Char::isLetter) } -> ""
                // A bare number is a duplicate-download marker, not a title.
                tokens.all { token -> token.all(Char::isDigit) } -> ""
                // Real words that simply are not in the vocabulary above, such as
                // "(Special Anniversary)", are safer kept than discarded.
                else -> match.value
            }
        }
}
