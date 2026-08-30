package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.PronunciationRule
import io.kotest.property.Arb
import io.kotest.property.PropTestConfig
import io.kotest.property.arbitrary.int
import io.kotest.property.arbitrary.list
import io.kotest.property.arbitrary.of
import io.kotest.property.arbitrary.string
import io.kotest.property.checkAll
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class PronunciationRulesTest {

    // region the cascade the single pass exists to prevent

    /**
     * The case that decides the whole design. A reader records "Rhysand" said "REE-sand", and
     * separately "and" said "AND" for emphasis.
     *
     * A sequence of independent replacements produces "REE-sAND": the second rule fires on the
     * "and" inside the first rule's output. One left-to-right pass cannot, because the cursor
     * never re-enters what it just wrote.
     */
    @Test
    fun `one rule never rewrites another rule's output`() {
        val rules = PronunciationRules.scoped(
            bookRules = listOf(rule("Rhysand", "REE-sand", 0), rule("and", "AND", 1)),
            accountRules = emptyList(),
        )

        val spoken = PronunciationRules.apply("Rhysand went home.", rules)

        assertEquals("REE-sand went home.", spoken)
        assertFalse("a rule rewrote another rule's output", spoken.contains("REE-sAND"))
    }

    /** The same rules still apply to a genuine standalone "and". */
    @Test
    fun `a later rule still applies where it legitimately matches`() {
        val rules = PronunciationRules.scoped(
            listOf(rule("Rhysand", "REE-sand", 0), rule("and", "AND", 1)), emptyList(),
        )
        assertEquals(
            "REE-sand AND Feyre",
            PronunciationRules.apply("Rhysand and Feyre", rules),
        )
    }

    // endregion

    // region boundaries

    /**
     * A rule for "and" must not fire inside "Rhysand" or "sandwich". This is the mistake that
     * makes a naive find-and-replace unusable on prose.
     */
    @Test
    fun `a rule matches only on non-alphanumeric boundaries`() {
        val rules = PronunciationRules.scoped(listOf(rule("and", "AND", 0)), emptyList())

        assertEquals("AND", PronunciationRules.apply("and", rules))
        assertEquals("Rhysand", PronunciationRules.apply("Rhysand", rules))
        assertEquals("sandwich", PronunciationRules.apply("sandwich", rules))
        assertEquals("brandy", PronunciationRules.apply("brandy", rules))
        assertEquals("AND, AND.", PronunciationRules.apply("and, and.", rules))
        assertEquals("(AND)", PronunciationRules.apply("(and)", rules))
    }

    /** A digit is a word character too, so a rule must not fire inside "and2". */
    @Test
    fun `a digit boundary blocks a match`() {
        val rules = PronunciationRules.scoped(listOf(rule("and", "AND", 0)), emptyList())
        assertEquals("and2", PronunciationRules.apply("and2", rules))
        assertEquals("2and", PronunciationRules.apply("2and", rules))
    }

    /** A listener typing "rhysand" means the name wherever it appears. */
    @Test
    fun `matching is case-insensitive`() {
        val rules = PronunciationRules.scoped(listOf(rule("rhysand", "REE-sand", 0)), emptyList())
        assertEquals("REE-sand", PronunciationRules.apply("Rhysand", rules))
        assertEquals("REE-sand", PronunciationRules.apply("RHYSAND", rules))
    }

    /**
     * A rule cannot bridge a removed filtered range, because by the time text arrives here the
     * filtered characters are gone and the two sides are in separate units.
     */
    @Test
    fun `no match is made across a removal boundary`() {
        val rules = PronunciationRules.scoped(listOf(rule("Rhysand", "REE-sand", 0)), emptyList())

        // "Rhy" ended one unit and "sand" began the next, because the filter removed what was
        // between them. Neither half matches, and no substitution is invented.
        val spoken = PronunciationRules.applyToUnits(listOf("Rhy", "sand walked"), rules)

        assertEquals(listOf("Rhy", "sand walked"), spoken)
    }

    // endregion

    // region precedence

    /**
     * Book scope wins over account scope: a rule recorded for this book is the more specific
     * statement about it.
     */
    @Test
    fun `book scope takes precedence over account scope`() {
        val rules = PronunciationRules.scoped(
            bookRules = listOf(rule("Rhysand", "book-form", 0)),
            accountRules = listOf(rule("Rhysand", "account-form", 0)),
        )
        assertEquals("book-form", PronunciationRules.apply("Rhysand", rules))
    }

    /**
     * Within a scope, earlier-recorded wins. Deliberately not longest-match: recording order is
     * something a listener can see and change, and match length is not.
     */
    @Test
    fun `an earlier rule wins over a later one in the same scope`() {
        // Both genuinely match at position 0: "New" is followed by a space, which is a valid
        // boundary, and "New York" matches the longer span. Recording order decides.
        val shortFirst = PronunciationRules.scoped(
            listOf(rule("New", "NOO", 0), rule("New York", "NEW-YORK", 1)), emptyList(),
        )
        assertEquals("NOO York City", PronunciationRules.apply("New York City", shortFirst))

        val longFirst = PronunciationRules.scoped(
            listOf(rule("New York", "NEW-YORK", 0), rule("New", "NOO", 1)), emptyList(),
        )
        assertEquals("NEW-YORK City", PronunciationRules.apply("New York City", longFirst))
    }

    /**
     * The boundary rule is what makes longest-match mostly moot: a shorter rule cannot fire
     * inside a longer word at all, so the two only ever compete where the shorter form ends on
     * a real boundary. Recorded because it is easy to mistake for a precedence bug.
     */
    @Test
    fun `a shorter rule cannot win inside a longer word`() {
        val rules = PronunciationRules.scoped(
            listOf(rule("Rhys", "FIRST", 0), rule("Rhysand", "SECOND", 1)), emptyList(),
        )
        // "Rhys" is followed by "a", so it does not match at all; only "Rhysand" does.
        assertEquals("SECOND", PronunciationRules.apply("Rhysand", rules))
        // And on its own, with a real boundary, the earlier rule applies as recorded.
        assertEquals("FIRST said", PronunciationRules.apply("Rhys said", rules))
    }

    // endregion

    // region the property that matters

    /**
     * At most one rule applies per character, and no rule ever sees another's output.
     *
     * Checked by construction: every replacement is a marker carrying its rule's index, so the
     * output can be read back to confirm no marker sits inside another marker's span. A naive
     * sequential implementation fails this immediately.
     */
    @Test
    fun `no rule applies to characters another rule substituted`(): Unit = runBlocking {
        val words = Arb.of("and", "sand", "Rhysand", "the", "there", "a", "an", "band")
        checkAll(
            PropTestConfig(iterations = 200),
            Arb.list(words, 1..6),
            Arb.list(words, 1..12),
        ) { ruleWords, textWords ->
            val distinct = ruleWords.distinct()
            val rules = PronunciationRules.scoped(
                distinct.mapIndexed { index, word -> rule(word, "<$index>", index) },
                emptyList(),
            )
            val text = textWords.joinToString(" ")

            val spoken = PronunciationRules.apply(text, rules)

            // Every marker in the output is well-formed and complete: a nested or partial
            // marker is exactly what a rule rewriting another rule's output produces.
            val markers = Regex("<(\\d+)>").findAll(spoken).toList()
            markers.forEach { marker ->
                val index = marker.groupValues[1].toInt()
                assertTrue(
                    "output '$spoken' holds a marker for a rule that does not exist",
                    index in distinct.indices,
                )
            }
            // No stray angle brackets, which is what a partially-rewritten marker leaves.
            val bracketCount = spoken.count { it == '<' }
            assertEquals(
                "output '$spoken' has mismatched markers, so a substitution was rewritten",
                bracketCount,
                markers.size,
            )
        }
    }

    /** Applying rules to text they do not match changes nothing at all. */
    @Test
    fun `text with no matches is returned unchanged`(): Unit = runBlocking {
        checkAll(PropTestConfig(iterations = 120), Arb.string(0..80)) { text ->
            val rules = PronunciationRules.scoped(
                listOf(rule("zzqqxx-not-present", "replaced", 0)), emptyList(),
            )
            assertEquals(text, PronunciationRules.apply(text, rules))
        }
    }

    @Test
    fun `no rules leaves text alone`(): Unit = runBlocking {
        checkAll(PropTestConfig(iterations = 60), Arb.string(0..60)) { text ->
            assertEquals(text, PronunciationRules.apply(text, emptyList()))
        }
    }

    // endregion

    // region validation

    @Test
    fun `an empty written form is rejected naming that form`() {
        val rejection = PronunciationRules.validate("  ", "spoken", emptyList())
        val bounds = rejection as RuleRejection.OutOfBounds
        assertEquals(RuleRejection.OutOfBounds.Form.WRITTEN, bounds.form)
        assertTrue(bounds.message.contains("as it appears in the book"))
    }

    @Test
    fun `an empty spoken form is rejected naming that form`() {
        val rejection = PronunciationRules.validate("Rhysand", "", emptyList())
        assertEquals(
            RuleRejection.OutOfBounds.Form.SPOKEN,
            (rejection as RuleRejection.OutOfBounds).form,
        )
    }

    @Test
    fun `an over-long form is rejected naming which one`() {
        val long = "x".repeat(PronunciationRules.MAXIMUM_WRITTEN_CHARACTERS + 1)
        assertEquals(
            RuleRejection.OutOfBounds.Form.WRITTEN,
            (PronunciationRules.validate(long, "ok", emptyList()) as RuleRejection.OutOfBounds).form,
        )
        assertEquals(
            RuleRejection.OutOfBounds.Form.SPOKEN,
            (PronunciationRules.validate("ok", long, emptyList()) as RuleRejection.OutOfBounds).form,
        )
    }

    /**
     * A case-insensitive duplicate is refused and the existing rule offered instead, because
     * holding both would make the outcome depend on recording order for no visible reason.
     */
    @Test
    fun `a case-insensitive duplicate is refused and offers the existing rule`() {
        val existing = rule("Rhysand", "REE-sand", 0)
        val rejection = PronunciationRules.validate("rhysand", "something else", listOf(existing))
        val duplicate = rejection as RuleRejection.Duplicate
        assertEquals(existing, duplicate.existing)
        assertTrue(duplicate.message.contains("Rhysand"))
    }

    /** Editing the rule you are already on is not a duplicate of itself. */
    @Test
    fun `editing an existing rule is not a duplicate`() {
        val existing = rule("Rhysand", "REE-sand", 0)
        assertNull(
            PronunciationRules.validate(
                "Rhysand", "RYE-sand", listOf(existing), editingWritten = "Rhysand",
            ),
        )
    }

    @Test
    fun `a full scope refuses a new rule`() {
        val full = (0 until PronunciationRules.MAXIMUM_RULES_PER_SCOPE)
            .map { rule("word$it", "spoken$it", it) }
        val rejection = PronunciationRules.validate("another", "spoken", full)
        assertEquals(
            PronunciationRules.MAXIMUM_RULES_PER_SCOPE,
            (rejection as RuleRejection.ScopeFull).limit,
        )
    }

    /** An edit replaces rather than adds, so a full scope must still allow one. */
    @Test
    fun `a full scope still allows an edit`() {
        val full = (0 until PronunciationRules.MAXIMUM_RULES_PER_SCOPE)
            .map { rule("word$it", "spoken$it", it) }
        assertNull(
            PronunciationRules.validate(
                "word0", "a new pronunciation", full, editingWritten = "word0",
            ),
        )
    }

    @Test
    fun `a valid rule is accepted`() {
        assertNull(PronunciationRules.validate("Rhysand", "REE-sand", emptyList()))
    }

    // endregion

    // region re-render impact

    /**
     * The count is what changing a rule costs, and it uses the same matching as application,
     * so it cannot offer to re-render a chapter that would come out identical.
     */
    @Test
    fun `only chapters holding a real match are affected`() {
        val chapters = mapOf(
            0 to listOf("Rhysand walked in."),
            1 to listOf("Nothing to see here."),
            2 to listOf("The sandwich was good."),
            3 to listOf("Then Rhysand left."),
        )

        val impact = PronunciationRules.rerenderImpact(chapters, rule("Rhysand", "REE-sand", 0))

        assertEquals(listOf(0, 3), impact.affectedChapterIndices)
        assertEquals(2, impact.chapterCount)
        assertFalse(impact.isEmpty)
    }

    /** "sandwich" must not count as a match for "sand", or nothing would ever be re-rendered. */
    @Test
    fun `a substring occurrence is not an affected chapter`() {
        val impact = PronunciationRules.rerenderImpact(
            mapOf(0 to listOf("The sandwich was good.")),
            rule("sand", "SAND", 0),
        )
        assertTrue(impact.isEmpty)
    }

    // endregion

    // region fingerprint

    /**
     * Order is part of the fingerprint because order decides precedence, so the same rules in a
     * different order genuinely produce different audio.
     */
    @Test
    fun `reordering rules changes the fingerprint`() {
        val one = PronunciationRules.scoped(
            listOf(rule("a", "A", 0), rule("b", "B", 1)), emptyList(),
        )
        val other = PronunciationRules.scoped(
            listOf(rule("b", "B", 0), rule("a", "A", 1)), emptyList(),
        )
        assertFalse(
            "two orderings produced the same fingerprint",
            PronunciationRules.fingerprint(one) == PronunciationRules.fingerprint(other),
        )
    }

    @Test
    fun `the same rules produce the same fingerprint`() {
        val build = {
            PronunciationRules.scoped(
                listOf(rule("Rhysand", "REE-sand", 0)), listOf(rule("Feyre", "FAY-ruh", 0)),
            )
        }
        assertEquals(
            PronunciationRules.fingerprint(build()),
            PronunciationRules.fingerprint(build()),
        )
    }

    @Test
    fun `no rules fingerprints to nothing`() {
        assertEquals("", PronunciationRules.fingerprint(emptyList()))
    }

    /** Scope is part of it: the same word in a different scope is a different arrangement. */
    @Test
    fun `moving a rule between scopes changes the fingerprint`() {
        val asBook = PronunciationRules.scoped(listOf(rule("a", "A", 0)), emptyList())
        val asAccount = PronunciationRules.scoped(emptyList(), listOf(rule("a", "A", 0)))
        assertFalse(
            PronunciationRules.fingerprint(asBook) == PronunciationRules.fingerprint(asAccount),
        )
    }

    // endregion

    // region generators and fixtures

    private fun rule(written: String, spoken: String, order: Int) =
        PronunciationRule(writtenForm = written, replacementForm = spoken, order = order)
}
