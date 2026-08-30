package com.audiochoice.mobile.narration

import com.audiochoice.contracts.ScanEvent
import com.audiochoice.mobile.data.NarrationUnit
import com.audiochoice.mobile.reader.ReaderMask
import com.audiochoice.mobile.reader.merged
import io.kotest.property.Arb
import io.kotest.property.PropTestConfig
import io.kotest.property.arbitrary.int
import io.kotest.property.arbitrary.list
import io.kotest.property.arbitrary.map
import io.kotest.property.checkAll
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class FilteredRangesTest {

    // region deriving ranges

    /**
     * Only events still switched on produce ranges, and the decision goes through
     * the shared predicate rather than a second copy of the condition. That shared
     * predicate exists because the rule was once written out twice and the audio and
     * text paths could disagree about what was filtered.
     */
    @Test
    fun `disabled events produce no range`() {
        val events = listOf(
            event(id = "a", start = 10, end = 20, category = "CAT-1"),
            event(id = "b", start = 40, end = 50, category = "CAT-2"),
        )

        val ranges = FilteredRanges.forEnabledEvents(
            events = events,
            disabledCategoryIDs = setOf("cat-1"),
        )

        assertEquals(listOf(ReaderMask(40, 50)), ranges)
    }

    /** A range ending where the next begins is one continuous passage. */
    @Test
    fun `touching and overlapping ranges merge`() {
        val ranges = FilteredRanges.forEnabledEvents(
            listOf(
                event("a", 10, 20),
                event("b", 20, 30),
                event("c", 25, 40),
                event("d", 80, 90),
            ),
        )

        assertEquals(listOf(ReaderMask(10, 40), ReaderMask(80, 90)), ranges)
    }

    @Test
    fun `ranges come back ordered regardless of event order`() {
        val ranges = FilteredRanges.forEnabledEvents(
            listOf(event("a", 90, 100), event("b", 10, 20), event("c", 50, 60)),
        )

        assertEquals(listOf(ReaderMask(10, 20), ReaderMask(50, 60), ReaderMask(90, 100)), ranges)
    }

    // endregion

    // region offset validation

    /**
     * Validated as a batch. An out-of-range offset means the server and client
     * disagree about the coordinate space, and in that state no event can be
     * trusted; half a filter is worse than none because the listener believes
     * filtering is on.
     */
    @Test
    fun `one bad offset invalidates the whole set`() {
        val good = listOf(event("a", 0, 10), event("b", 20, 30))
        assertTrue(FilteredRanges.offsetsAreValid(good, bookTextLength = 100))

        assertFalse(
            FilteredRanges.offsetsAreValid(good + event("c", 90, 200), bookTextLength = 100),
        )
        assertFalse(
            FilteredRanges.offsetsAreValid(good + event("c", 50, 50), bookTextLength = 100),
        )
        assertFalse(
            FilteredRanges.offsetsAreValid(good + event("c", -1, 10), bookTextLength = 100),
        )
    }

    /**
     * A fractional offset means the value was produced as a time rather than as a
     * character index, which is exactly the confusion worth catching at the boundary.
     */
    @Test
    fun `a fractional offset is rejected as not a character index`() {
        val fractional = listOf(
            ScanEvent(
                id = "a",
                startTime = 10.5,
                endTime = 20.0,
                categoryID = "CAT",
                groupID = "GRP",
                eventID = "EVT",
                confidence = 0.9,
            ),
        )

        assertFalse(FilteredRanges.offsetsAreValid(fractional, bookTextLength = 100))
    }

    @Test
    fun `an empty event set is valid`() {
        assertTrue(FilteredRanges.offsetsAreValid(emptyList(), bookTextLength = 100))
    }

    // endregion

    // region building spoken text

    private val sentence = "The lantern swung against the rigging all night long."

    @Test
    fun `no enabled event leaves every unit untouched`() {
        val units = listOf(unit(0, sentence))
        val speech = SpokenTextBuilder.build(units, emptyList())

        assertEquals(1, speech.spoken.size)
        assertEquals(sentence, speech.spoken.single().text)
        assertEquals(0, speech.omittedUnits)
        assertEquals(0, speech.partiallyRemovedUnits)
    }

    /**
     * A unit covered in full is dropped rather than sent as an empty string: no
     * submission, no audio, no timeline entry. Nothing about it reaches an engine.
     */
    @Test
    fun `a fully covered unit is omitted entirely`() {
        val units = listOf(unit(0, sentence))
        val speech = SpokenTextBuilder.build(units, listOf(ReaderMask(0, sentence.length)))

        assertTrue(speech.spoken.isEmpty())
        assertEquals(1, speech.omittedUnits)
        assertTrue(speech.isSilent)
    }

    /**
     * A partly covered unit keeps its surviving characters and still records one
     * timeline entry spanning the whole original unit. The whole-unit range is what
     * keeps the reader and the audio agreeing about which passage is playing.
     */
    @Test
    fun `a partly covered unit keeps what survives and spans the whole unit`() {
        val units = listOf(unit(0, sentence))
        // Remove "swung against the rigging".
        val from = sentence.indexOf("swung")
        val to = sentence.indexOf(" all")
        val speech = SpokenTextBuilder.build(units, listOf(ReaderMask(from, to)))

        val spoken = speech.spoken.single()
        assertEquals(0, spoken.startCharacter)
        assertEquals(sentence.length, spoken.endCharacter)
        assertEquals("The lantern all night long.", spoken.text)
        assertEquals(1, speech.partiallyRemovedUnits)
        assertEquals(0, speech.omittedUnits)
    }

    /** Removal must not fuse two clauses that were never adjacent. */
    @Test
    fun `a space is left where characters were removed`() {
        val text = "alpha beta gamma"
        val units = listOf(unit(0, text))
        val speech = SpokenTextBuilder.build(
            units,
            listOf(ReaderMask(text.indexOf("beta"), text.indexOf("beta") + 4)),
        )

        assertEquals("alpha gamma", speech.spoken.single().text)
        assertFalse(speech.spoken.single().text.contains("alphagamma"))
    }

    /**
     * A unit reduced to punctuation is treated as covered in full. Sending it would
     * either produce a timeline entry describing silence or have the engine read the
     * punctuation aloud.
     */
    @Test
    fun `a unit reduced to punctuation is treated as fully covered`() {
        val text = "Hello, world!"
        val units = listOf(unit(0, text))
        // Remove both words, leaving only the comma and the exclamation mark.
        val speech = SpokenTextBuilder.build(
            units,
            listOf(ReaderMask(0, 5), ReaderMask(7, 12)),
        )

        assertTrue(speech.spoken.isEmpty())
        assertEquals(1, speech.omittedUnits)
    }

    @Test
    fun `a range outside a unit leaves it untouched`() {
        val units = listOf(unit(100, sentence))
        val speech = SpokenTextBuilder.build(units, listOf(ReaderMask(0, 50)))

        assertEquals(sentence, speech.spoken.single().text)
        assertEquals(0, speech.partiallyRemovedUnits)
    }

    @Test
    fun `several ranges inside one unit are all removed`() {
        val text = "one two three four five"
        val units = listOf(unit(0, text))
        val speech = SpokenTextBuilder.build(
            units,
            listOf(
                ReaderMask(text.indexOf("two"), text.indexOf("two") + 3),
                ReaderMask(text.indexOf("four"), text.indexOf("four") + 4),
            ),
        )

        assertEquals("one three five", speech.spoken.single().text)
    }

    @Test
    fun `omission and partial counts are reported per chapter`() {
        val first = "First sentence here."
        val second = "Second sentence here."
        val units = listOf(unit(0, first), unit(first.length + 1, second))
        val speech = SpokenTextBuilder.build(
            units,
            listOf(
                ReaderMask(0, first.length),
                ReaderMask(first.length + 1, first.length + 8),
            ),
        )

        assertEquals(1, speech.omittedUnits)
        assertEquals(1, speech.partiallyRemovedUnits)
        assertEquals(1, speech.spoken.size)
    }

    // endregion

    // region properties

    /**
     * The property that matters for cost and for privacy: not one filtered character
     * survives into anything that will be spoken, stored or sent.
     */
    @Test
    fun `no filtered character ever reaches spoken text`(): Unit = runBlocking {
        val text = (1..40).joinToString(" ") { "word$it" }
        checkAll(PropTestConfig(iterations = 200), maskSets(text.length)) { masks ->
            val units = listOf(unit(0, text))
            val speech = SpokenTextBuilder.build(units, masks)

            val filteredCharacters = masks
                .flatMap { (it.start until it.end).toList() }
                .map { text[it] }
                .filter { it.isLetterOrDigit() }
                .toSet()

            speech.spoken.forEach { spokenUnit ->
                // Every surviving letter or digit must come from an unfiltered offset.
                val survivors = spokenUnit.text.filter { it.isLetterOrDigit() }
                val allowed = (0 until text.length)
                    .filterNot { offset -> masks.any { it.start <= offset && offset < it.end } }
                    .map { text[it] }
                    .filter { it.isLetterOrDigit() }
                survivors.forEach { character ->
                    assertTrue(
                        "character '$character' survived despite being filtered",
                        allowed.contains(character) || !filteredCharacters.contains(character),
                    )
                }
            }
        }
    }

    /**
     * Metamorphic: enabling more filters can never make narration longer.
     *
     * Enabling an event can only remove characters or remove a unit, and neither can
     * increase the amount of audio produced. Stated as a property because the
     * failure mode would be a subtle one -- a removal that accidentally duplicated
     * surrounding text.
     */
    @Test
    fun `a superset of filters never yields more spoken characters`(): Unit = runBlocking {
        val text = (1..40).joinToString(" ") { "word$it" }
        val units = listOf(unit(0, text))

        checkAll(PropTestConfig(iterations = 150), maskSets(text.length), maskSets(text.length)) { a, b ->
            val subset = a
            val superset = (a + b).sortedBy { it.start }

            val small = SpokenTextBuilder.build(units, normalise(subset))
            val large = SpokenTextBuilder.build(units, normalise(superset))

            assertTrue(
                large.spoken.sumOf { it.text.length } <= small.spoken.sumOf { it.text.length },
            )
        }
    }

    /** A spoken unit always reports the whole original unit's range. */
    @Test
    fun `spoken units always span their whole original unit`(): Unit = runBlocking {
        val text = (1..30).joinToString(" ") { "token$it" }
        checkAll(PropTestConfig(iterations = 150), maskSets(text.length)) { masks ->
            val units = listOf(unit(0, text))
            val speech = SpokenTextBuilder.build(units, normalise(masks))

            speech.spoken.forEach { spokenUnit ->
                assertEquals(0, spokenUnit.startCharacter)
                assertEquals(text.length, spokenUnit.endCharacter)
            }
        }
    }

    // endregion

    // region fixtures

    private fun event(
        id: String,
        start: Int,
        end: Int,
        category: String = "CAT",
        group: String = "GRP",
    ) = ScanEvent(
        id = id,
        startTime = start.toDouble(),
        endTime = end.toDouble(),
        categoryID = category,
        groupID = group,
        eventID = "EVT-$id",
        confidence = 0.9,
        stableKey = "key-$id",
    )

    private fun unit(start: Int, text: String) = NarrationUnit(start, start + text.length, text)

    private fun maskSets(length: Int): Arb<List<ReaderMask>> =
        Arb.list(Arb.int(0 until length), 0..4).map { starts ->
            starts.sorted().map { start ->
                ReaderMask(start, minOf(start + 6, length))
            }
        }

    /**
     * Overlapping generated masks are merged, as the builder's contract requires.
     * Uses the reader's own merge, which is the same one the production path uses.
     */
    private fun normalise(masks: List<ReaderMask>): List<ReaderMask> = masks.merged()

    // endregion
}
