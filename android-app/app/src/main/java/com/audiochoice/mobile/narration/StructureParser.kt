package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.NarrationChapter
import com.audiochoice.mobile.data.NarrationPlan
import com.audiochoice.mobile.data.PlanInputs
import com.audiochoice.mobile.data.SourceRange
import com.audiochoice.mobile.reader.EpubDocument
import com.audiochoice.mobile.reader.NavigationEntry
import com.audiochoice.mobile.reader.NavigationOutline
import com.audiochoice.mobile.reader.NavigationSource

/**
 * Divides Book_Text into the chapters a listener will see and navigate.
 *
 * Only top-level navigation entries become chapters. A book that lists every scene
 * break in its contents would otherwise produce hundreds of chapters, each its own
 * render job and its own row in the chapter control, which serves the listener
 * worse than the divisions the author actually named.
 *
 * Coverage and non-overlap are not validated after the fact, they are produced by
 * construction: boundaries are sorted, each chapter ends where the next begins, the
 * first starts at zero and the last ends at the end of the text. There is no
 * arrangement of navigation entries that can yield a gap or an overlap, so no
 * downstream code has to handle one.
 */
object StructureParser {

    /**
     * Above this, a contents list is being used as an index rather than as chapters.
     * Falling back to the spine gives a listener a usable chapter control instead of
     * a scroll of thousands of entries, each of which would also be a render job.
     */
    const val MAXIMUM_DERIVED_CHAPTERS = 2_000

    /**
     * The largest a chapter may be before its nested divisions are used instead.
     *
     * A chapter is rendered in full before any of it plays, so this is a bound on waiting rather
     * than a formatting preference. At the rates measured on a real device -- about 18 spoken
     * characters a second, synthesised around 28 times faster than real time -- 60,000 characters is
     * roughly 55 minutes of audio and two minutes of synthesis. That is the upper end of a
     * believable single chapter, and anything past it is a Part.
     */
    const val MAXIMUM_CHAPTER_CHARACTERS = 60_000

    /** Bounds the descent, since each level is re-measured after expanding. */
    private const val MAXIMUM_EXPANSION_DEPTH = 6

    fun deriveChapters(document: EpubDocument): ChapterOutline {
        if (document.text.isEmpty() || document.resources.isEmpty()) {
            return ChapterOutline(emptyList(), NavigationSource.SPINE_FALLBACK, false)
        }

        val fromNavigation = document.navigation?.let { boundariesFromNavigation(document, it) }
        val usable = fromNavigation != null &&
            fromNavigation.isNotEmpty() &&
            fromNavigation.size <= MAXIMUM_DERIVED_CHAPTERS

        if (usable) {
            return ChapterOutline(
                chapters = close(fromNavigation!!, document.text.length),
                source = document.navigation!!.source,
                fellBackToSpine = false,
            )
        }

        return ChapterOutline(
            chapters = close(boundariesFromSpine(document), document.text.length),
            source = NavigationSource.SPINE_FALLBACK,
            // Only a fallback if the book declared a contents source and it could
            // not be used. A book that simply has no contents list is the ordinary
            // third case, not a degraded one, and recording it as degraded would put
            // a warning in front of a listener for a perfectly normal file.
            //
            // The test is on the declaration rather than on the parsed outline,
            // because a navigation document that exists and will not parse is
            // exactly the degraded case worth recording, and by then the parsed
            // outline is null.
            fellBackToSpine = document.declaresNavigation,
        )
    }

    /**
     * Turn navigation entries into start offsets.
     *
     * An entry whose target is not a spine document that contributed text is
     * dropped: books routinely point at a cover image or at a page that was left
     * out of the spine, and a chapter with no text is worse than a missing entry.
     */
    private fun boundariesFromNavigation(
        document: EpubDocument,
        navigation: NavigationOutline,
    ): List<Boundary> {
        val spans = document.resources.associateBy { it.entryName }
        // Expanded before boundaries are taken, so a division too large to be a render unit is
        // replaced by the divisions inside it.
        return flattenToRenderableDepth(navigation.entries, document).mapNotNull { entry ->
            val span = spans[entry.targetEntry] ?: return@mapNotNull null
            // A fragment is what lets two chapters share one spine document, which
            // is the normal shape of a single-file EPUB.
            val start = entry.targetFragment
                ?.let { document.anchorOffsets["${entry.targetEntry}#$it"] }
                ?.takeIf { it in span.range.start..span.range.end }
                ?: span.range.start
            Boundary(start = start, title = entry.title)
        }
    }

    /**
     * Replaces any division too large to render with the divisions nested inside it.
     *
     * A chapter is the unit that has to be synthesised in full before a word of it can be heard, so
     * its size is a limit on how long the listener waits, not just a matter of how the contents list
     * is organised. Plenty of books name Parts at the top level: one real book's first Part came to
     * 440,000 spoken characters, about seven hours of audio and a quarter of an hour of synthesis
     * before playback could begin, which reads as a button that does nothing.
     *
     * Top-level entries are still preferred wherever they are already a sensible size. A book whose
     * contents list names every scene break keeps the chapters its author named, which is what the
     * top-level-only rule was protecting and worth keeping.
     *
     * Descends only where there is somewhere to descend to. A single enormous chapter with no
     * nested entries stays one chapter, because inventing divisions the author did not name would
     * put chapter rows in front of the listener that correspond to nothing in their book.
     */
    private fun flattenToRenderableDepth(
        entries: List<NavigationEntry>,
        document: EpubDocument,
        depth: Int = 0,
    ): List<NavigationEntry> {
        if (depth >= MAXIMUM_EXPANSION_DEPTH) return entries
        // Sizes come from where each entry starts, so they are measured the same way the boundaries
        // that follow will be. Measured across the whole list at once because an entry's extent is
        // defined by where the next one begins.
        val starts: List<Int?> = entries.map { entry -> startOf(entry, document) }
        val result = mutableListOf<NavigationEntry>()
        var expanded = false
        entries.forEachIndexed { index, entry ->
            val start = starts[index]
            // An entry whose target is missing cannot be measured, so it is left alone rather than
            // guessed at.
            val extent = if (start == null) {
                null
            } else {
                val next = starts.drop(index + 1).filterNotNull().firstOrNull { it > start }
                (next ?: document.text.length) - start
            }
            if (extent != null && extent > MAXIMUM_CHAPTER_CHARACTERS && entry.children.isNotEmpty()) {
                expanded = true
                // The parent is kept as the first division of itself. Its own text -- a Part title,
                // an epigraph opening the Part -- lies before the first child begins and would
                // otherwise belong to no chapter and never be spoken.
                result += entry.copy(children = emptyList())
                result += flattenToRenderableDepth(entry.children, document, depth + 1)
            } else {
                result += entry.copy(children = emptyList())
            }
        }
        // Re-measured after expanding, because a child can itself be a Part.
        return if (expanded) flattenToRenderableDepth(result, document, depth + 1) else result
    }

    private fun startOf(entry: NavigationEntry, document: EpubDocument): Int? {
        val span = document.resources.firstOrNull { it.entryName == entry.targetEntry } ?: return null
        return entry.targetFragment
            ?.let { document.anchorOffsets["${entry.targetEntry}#$it"] }
            ?.takeIf { it in span.range.start..span.range.end }
            ?: span.range.start
    }

    private fun boundariesFromSpine(document: EpubDocument): List<Boundary> =
        document.resources.map { Boundary(start = it.range.start, title = null) }

    /**
     * Close the boundaries into contiguous chapter ranges.
     *
     * Sorting by start offset is also sorting by spine order, because Book_Text is
     * assembled in spine order, so one sort satisfies both ordering rules.
     *
     * Text before the first derived boundary joins the first chapter rather than
     * becoming a chapter of its own. It is almost always a stray page the contents
     * list did not mention, and giving it its own row would put an untitled entry
     * at the top of every book's chapter list.
     */
    private fun close(boundaries: List<Boundary>, textLength: Int): List<NarrationChapter> {
        if (boundaries.isEmpty()) return emptyList()

        // Deduplicate by offset, keeping the first title seen there. Two entries
        // pointing at the same place is common when a book links both a part
        // heading and its first chapter to the same page.
        val byStart = sortedMapOf<Int, String?>()
        boundaries.sortedBy { it.start }.forEach { boundary ->
            val clamped = boundary.start.coerceIn(0, textLength)
            if (!byStart.containsKey(clamped)) byStart[clamped] = boundary.title
        }

        val starts = byStart.keys.toMutableList()
        val titles = byStart.values.toMutableList()
        // The first chapter always begins at zero, so every character is covered.
        starts[0] = 0

        val chapters = mutableListOf<NarrationChapter>()
        starts.indices.forEach { index ->
            val start = starts[index]
            val end = starts.getOrNull(index + 1) ?: textLength
            if (end <= start) return@forEach
            chapters += NarrationChapter(
                index = chapters.size,
                title = title(titles[index], chapters.size + 1),
                startCharacter = start,
                endCharacter = end,
            )
        }
        return chapters
    }

    /**
     * A trimmed, whitespace-collapsed, length-capped title, or the ordinal.
     *
     * Contents entries carry line breaks and runs of spaces from their markup, and
     * an occasional entry is a whole paragraph, so a cap keeps the chapter control
     * readable.
     */
    internal fun title(source: String?, ordinal: Int): String {
        val cleaned = source
            ?.replace(Regex("\\s+"), " ")
            ?.trim()
            ?.takeIf { it.isNotBlank() }
            ?: return "Chapter $ordinal"
        return if (cleaned.length <= MAXIMUM_TITLE_LENGTH) {
            cleaned
        } else {
            cleaned.take(MAXIMUM_TITLE_LENGTH)
        }
    }

    const val MAXIMUM_TITLE_LENGTH = 200

    private data class Boundary(val start: Int, val title: String?)

    /**
     * Derive chapters, segment their prose, and assemble the plan.
     *
     * Returns null when the book yields no unit at all. That is a real outcome, not
     * an error: a picture book, a file whose text is images, or an archive that is
     * all front matter. Persisting an empty plan would leave a book in the library
     * that can never play and never explain why, so the caller reports that there is
     * no narratable prose and leaves the book unrendered.
     *
     * Construction is a pure function of its inputs. Nothing random, nothing
     * hash-ordered, nothing time-dependent enters it, because a plan that differed
     * between runs would look like a changed book and silently discard rendered
     * audio.
     */
    fun buildPlan(
        document: EpubDocument,
        sourceSha256: String,
        synthesisInputLimit: Int,
        enabledEventKeys: List<String> = emptyList(),
        pronunciationRuleFingerprint: String = "",
    ): NarrationPlan? {
        val outline = deriveChapters(document)
        if (outline.chapters.isEmpty()) return null

        val chapters = outline.chapters.map { chapter ->
            chapter.copy(
                units = UnitSegmenter.segment(
                    bookText = document.text,
                    chapterRange = SourceRange(chapter.startCharacter, chapter.endCharacter),
                    nonProseRanges = document.nonProseRanges,
                    limit = synthesisInputLimit,
                    language = document.language,
                ),
            )
        }

        // A chapter with no prose is kept, so the chapter control still shows the
        // division the author named, and marked as needing no rendering.
        if (chapters.none { it.requiresRendering }) return null

        return NarrationPlan(
            planVersion = NarrationPlan.PLAN_VERSION,
            inputs = PlanInputs(
                sourceSha256 = sourceSha256.lowercase(),
                bookTextHash = NarrationStore.bookTextHash(
                    bookText = document.text,
                    extractionVersion = document.extractionVersion,
                ),
                extractionVersion = document.extractionVersion,
                planVersion = NarrationPlan.PLAN_VERSION,
                synthesisInputLimit = synthesisInputLimit,
                enabledEventKeys = enabledEventKeys.sorted(),
                pronunciationRuleFingerprint = pronunciationRuleFingerprint,
            ),
            chapterDerivationFellBackToSpine = outline.fellBackToSpine,
            chapters = chapters,
        )
    }
}

/** Chapters derived from one document, and where they came from. */
data class ChapterOutline(
    val chapters: List<NarrationChapter>,
    val source: NavigationSource,
    /** True only when navigation existed and could not be used. */
    val fellBackToSpine: Boolean,
)
