package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.PronunciationRule
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * Pronunciation rules have to reach the voice.
 *
 * `NarrationRenderCoordinator` takes a `pronounce` function that defaults to the identity, and for a
 * long while nothing supplied one — so rules could be stored, loaded and listed while every one of
 * them was inert, and a comment in the view model claimed they had been applied. A rule that is
 * accepted and then ignored is worse than one that is refused.
 */
class PronunciationWiringTest {

    /** The default is the identity function, which is why omitting it fails silently. */
    @Test
    fun `the coordinator's pronounce parameter still defaults to the identity`() {
        assertTrue(
            "the default changed; if it is no longer the identity then the guard below is no " +
                "longer testing what makes omitting it dangerous",
            sourceOf(COORDINATOR).contains("private val pronounce: (String) -> String = { it },"),
        )
    }

    /** And the view model must actually pass one. */
    @Test
    fun `the view model supplies a real pronounce function`() {
        val source = sourceOf(VIEW_MODEL)
        assertTrue(
            "the view model does not supply pronounce, so the coordinator falls back to the " +
                "identity and every stored pronunciation rule is inert",
            source.contains("pronounce = {"),
        )
        assertTrue(
            "the supplied function does not apply the rules",
            source.contains("PronunciationRules.apply("),
        )
        assertTrue(
            "the rules are no longer loaded with the book, so a render would either wait on a " +
                "preferences read or proceed as though there were no rules",
            source.contains("PronunciationRules.scoped("),
        )
    }

    /**
     * Applied to spoken text only.
     *
     * The reader shows the book as written. A rule that changed the text on screen would be editing
     * someone's book rather than narrating it.
     */
    @Test
    fun `pronunciation is applied to spoken units and not to the book text`() {
        val source = sourceOf(COORDINATOR)
        assertTrue(
            "pronunciation is no longer applied to the spoken units",
            source.contains("speech.spoken.map { unit -> unit.copy(text = pronounce(unit.text)) }"),
        )
        // Book_Text is what the reader renders and what character offsets index into. Rewriting it
        // would move every scan event and every bookmark.
        assertTrue(
            "pronunciation now touches Book_Text, which would shift every character offset the " +
                "scan events and bookmarks are expressed in",
            !source.contains("pronounce(bookText") && !source.contains("pronounce(request.bookText"),
        )
    }

    /**
     * Book rules take precedence over account rules.
     *
     * Exercised for real rather than asserted on source: precedence is the part a listener would
     * notice being wrong, since a book-specific correction exists precisely to override a general one.
     */
    @Test
    fun `a book rule wins over an account rule for the same word`() {
        val scoped = PronunciationRules.scoped(
            bookRules = listOf(PronunciationRule("Rhysand", "REE-sand", order = 0)),
            accountRules = listOf(PronunciationRule("Rhysand", "rye-SAND", order = 0)),
        )
        assertEquals("REE-sand said it", PronunciationRules.apply("Rhysand said it", scoped))
    }

    /** With no rules the text is returned unchanged, which is what the identity default relied on. */
    @Test
    fun `no rules leaves the text alone`() {
        assertEquals(
            "Rhysand said it",
            PronunciationRules.apply("Rhysand said it", emptyList()),
        )
    }

    private fun sourceOf(relativePath: String): String {
        val file = listOf(
            File(relativePath),
            File("app/$relativePath"),
            File("../app/$relativePath"),
        ).firstOrNull(File::isFile)
        assertTrue(
            "could not locate $relativePath; this guard would otherwise pass without checking",
            file != null,
        )
        return file!!.readText()
    }

    private companion object {
        const val VIEW_MODEL =
            "src/main/java/com/audiochoice/mobile/narration/NarrationViewModel.kt"
        const val COORDINATOR =
            "src/main/java/com/audiochoice/mobile/narration/NarrationRenderCoordinator.kt"
    }
}
