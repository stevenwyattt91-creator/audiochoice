package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.PronunciationRule
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * Recording a pronunciation rule, and what happens to audio already made.
 *
 * The arithmetic lives in [PronunciationRules] and is exercised directly here. The wiring around it is
 * checked against source, because the view model needs a `Context` and the dialogue needs Robolectric.
 */
class PronunciationEditorTest {

    // region validation, exercised for real

    /** A refusal names which field is wrong, so the message can point at it. */
    @Test
    fun `an empty field is refused by name`() {
        val written = PronunciationRules.validate("", "REE-sand", emptyList())
        assertEquals(
            RuleRejection.OutOfBounds.Form.WRITTEN,
            (written as RuleRejection.OutOfBounds).form,
        )
        val spoken = PronunciationRules.validate("Rhysand", "   ", emptyList())
        assertEquals(
            RuleRejection.OutOfBounds.Form.SPOKEN,
            (spoken as RuleRejection.OutOfBounds).form,
        )
    }

    @Test
    fun `an over-long field is refused by name`() {
        val long = "a".repeat(PronunciationRule.MAXIMUM_FORM_LENGTH + 1)
        assertEquals(
            RuleRejection.OutOfBounds.Form.WRITTEN,
            (PronunciationRules.validate(long, "fine", emptyList()) as RuleRejection.OutOfBounds)
                .form,
        )
        assertEquals(
            RuleRejection.OutOfBounds.Form.SPOKEN,
            (PronunciationRules.validate("fine", long, emptyList()) as RuleRejection.OutOfBounds)
                .form,
        )
    }

    /** A duplicate is matched without regard to case, and hands back the rule that already exists. */
    @Test
    fun `a duplicate is refused case-insensitively and offers the existing rule`() {
        val existing = PronunciationRule("Rhysand", "REE-sand", order = 0)
        val rejection = PronunciationRules.validate("rhysand", "rye-SAND", listOf(existing))
        assertEquals(existing, (rejection as RuleRejection.Duplicate).existing)
    }

    /** Editing a rule is not a duplicate of itself. */
    @Test
    fun `replacing a rule is not treated as a duplicate`() {
        val existing = PronunciationRule("Rhysand", "REE-sand", order = 0)
        assertEquals(
            null,
            PronunciationRules.validate(
                written = "Rhysand",
                spoken = "rih-SAND",
                existingInScope = listOf(existing),
                editingWritten = "Rhysand",
            ),
        )
    }

    /** The per-scope limit refuses an addition but never disturbs what is stored. */
    @Test
    fun `a full scope is refused`() {
        val full = (0 until PronunciationRule.MAXIMUM_RULES_PER_SCOPE).map {
            PronunciationRule("word$it", "said$it", order = it)
        }
        val rejection = PronunciationRules.validate("another", "said", full)
        assertEquals(
            PronunciationRule.MAXIMUM_RULES_PER_SCOPE,
            (rejection as RuleRejection.ScopeFull).limit,
        )
        // An edit at the limit is still allowed, or a full scope could never be corrected.
        assertEquals(
            null,
            PronunciationRules.validate("word0", "different", full, editingWritten = "word0"),
        )
    }

    /** Only chapters holding a match are affected, under the same matching rule as rendering. */
    @Test
    fun `the re-render impact counts only chapters holding a match`() {
        val impact = PronunciationRules.rerenderImpact(
            chapterTexts = mapOf(
                0 to listOf("Rhysand walked in."),
                1 to listOf("Nobody by that name."),
                2 to listOf("She saw rhysand again."),
            ),
            rule = PronunciationRule("Rhysand", "REE-sand", order = 0),
        )
        assertEquals(listOf(0, 2), impact.affectedChapterIndices)
    }

    // endregion

    // region wiring

    /** Validation must precede persistence, or a refused rule is already stored. */
    @Test
    fun `a rule is validated before it is written`() {
        val body = functionBody("    fun recordPronunciationRule(")
        val validated = body.indexOf("PronunciationRules.validate(")
        val persisted = body.indexOf("persistPronunciationRules(")
        assertTrue("the rule is no longer validated", validated > 0)
        assertTrue("the rule is no longer persisted", persisted > 0)
        assertTrue("the rule is stored before it is validated", validated < persisted)
        assertTrue(
            "a refusal no longer stops the write",
            body.contains("if (rejection != null)") && body.contains("return@launch"),
        )
    }

    /**
     * A replacement keeps the position the rule already held.
     *
     * Order decides precedence, so moving a replaced rule to the end could change how a different
     * word is said.
     */
    @Test
    fun `replacing a rule keeps its position`() {
        assertTrue(
            "a replaced rule is appended rather than kept in place, which changes precedence",
            functionBody("    fun recordPronunciationRule(")
                .contains("order = replacedIndex ?: existing.size"),
        )
    }

    /** And a deletion must not shift precedence between the rules that remain. */
    @Test
    fun `deleting a rule reindexes the rest`() {
        assertTrue(
            "a deletion leaves a gap in the order, which changes precedence between the rules " +
                "that remain",
            functionBody("    fun deletePronunciationRule(")
                .contains("mapIndexed { index, rule -> rule.copy(order = index) }"),
        )
    }

    /** The re-render is offered, not performed: it costs the wait again. */
    @Test
    fun `recording a rule offers the re-render rather than starting it`() {
        val body = functionBody("    fun recordPronunciationRule(")
        assertTrue(
            "recording a rule no longer reports its effect on audio already made",
            body.contains("PronunciationRules.rerenderImpact("),
        )
        assertFalse(
            "recording a rule now discards audio without asking",
            body.contains("deleteChapterAudio(") || body.contains("rerenderChapters("),
        )
        // Nothing to say when nothing is affected, or every rule prompts.
        assertTrue(
            "an unaffected book still raises the offer",
            body.contains("impact.takeIf { !it.isEmpty }"),
        )
    }

    /**
     * A refused rule keeps what was typed.
     *
     * Recording is asynchronous, so clearing the fields on the button press throws away the entry
     * precisely when the rule turns out to be refused and the listener needs it back to correct it.
     * The form therefore clears on an acceptance signal from the view model, never on the press.
     */
    @Test
    fun `the form clears on acceptance rather than on the button press`() {
        val dialog = appFunctionBody("private fun NarrationPronunciationDialog(")
        val confirm = dialog.indexOf("onClick = { onRecord(written, spoken, scope, editing) }")
        assertTrue(
            "the confirm handler no longer simply records; if it clears the fields itself then a " +
                "refused rule loses what was typed",
            confirm > 0,
        )
        assertTrue(
            "the form no longer clears on an acceptance signal, so it would never reset after a " +
                "rule is accepted",
            dialog.contains("LaunchedEffect(state.pronunciationAccepted)"),
        )
        // A counter, because two rules accepted in a row must be distinguishable.
        assertTrue(
            "the acceptance signal is not counted, so a second acceptance would leave the fields " +
                "populated",
            functionBody("    fun recordPronunciationRule(")
                .contains("pronunciationAccepted = mutableState.value.pronunciationAccepted + 1"),
        )
    }

    /** Only rendered chapters are considered: an unrendered one will simply be made with the rule. */
    @Test
    fun `only rendered chapters are examined for matches`() {
        assertTrue(
            "unrendered chapters are counted as needing a re-render, which reports work that is " +
                "not work",
            functionBody("    private fun renderedChapterUnitTexts(")
                .contains("== RenderState.RENDERED"),
        )
    }

    /**
     * A preview is not part of the book.
     *
     * Written to the cache rather than the book's audio directory, or it would be counted as the
     * book's storage and could be mistaken for a rendered chapter.
     */
    @Test
    fun `a preview is not written into the book's audio`() {
        val body = functionBody("    fun previewPronunciation(")
        assertTrue(
            "the preview is no longer written to the cache, so it would be counted as the book's " +
                "audio",
            body.contains("context.cacheDir"),
        )
        assertFalse(
            "the preview is written into the book's own directory",
            body.contains("store.bookDirectory("),
        )
        // A preview must not bill the listener's premium allowance for a syllable.
        assertFalse(
            "the preview now goes through the premium voice, which bills per character and sends " +
                "text off the device for a syllable",
            body.contains("premiumEngine("),
        )
    }

    // endregion

    private fun appFunctionBody(declaration: String): String {
        val app = "src/main/java/com/audiochoice/mobile/ui/AudioChoiceApp.kt"
        val file = listOf(File(app), File("app/$app"), File("../app/$app"))
            .firstOrNull(File::isFile)
        assertTrue("could not locate $app", file != null)
        val source = file!!.readText()
        val start = source.indexOf(declaration)
        assertTrue("$declaration was not found", start >= 0)
        val end = source.indexOf("\n}\n", start)
        assertTrue("the end of $declaration was not found", end > start)
        return source.substring(start, end)
    }

    private fun functionBody(declaration: String): String {
        val file = listOf(
            File(VIEW_MODEL),
            File("app/$VIEW_MODEL"),
            File("../app/$VIEW_MODEL"),
        ).firstOrNull(File::isFile)
        assertTrue(
            "could not locate $VIEW_MODEL; this guard would otherwise pass without checking",
            file != null,
        )
        val source = file!!.readText()
        val start = source.indexOf(declaration)
        assertTrue("$declaration was not found", start >= 0)
        val end = source.indexOf("\n    }\n", start)
        assertTrue("the end of $declaration was not found", end > start)
        return source.substring(start, end)
    }

    private companion object {
        const val VIEW_MODEL =
            "src/main/java/com/audiochoice/mobile/narration/NarrationViewModel.kt"
    }
}
