package com.audiochoice.mobile.narration

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * Pins the separation between the two ways an EPUB enters the app.
 *
 * Attaching an EPUB to an imported audiobook is read-along: the audio already exists, the
 * EPUB supplies text to follow, and nothing is synthesised. Importing an EPUB on its own is
 * narration: there is no audio until a voice makes some. The two share the extraction code
 * and nothing else.
 *
 * Worth a test because the two are one plausible refactor apart. `attachEpub` already holds
 * a `Uri` to an EPUB and already extracts its text, so routing it into the importer looks
 * like deduplication. It would silently start synthesising audio for books that already have
 * a narrator, and charge premium synthesis for it.
 *
 * Checked against the source rather than by reflection because what matters is which types
 * the method reaches for, and a compiled method body does not expose that.
 */
class NarrationEntryPointsTest {

    @Test
    fun `attaching an EPUB to an audiobook does not reach into narration`() {
        val body = methodBody(PLAYER_VIEW_MODEL, "fun attachEpub")

        // Read-along persistence, which is what this path is for.
        assertTrue(
            "attachEpub no longer stores the EPUB for read-along, so this guard is no " +
                "longer testing the path it was written for",
            body.contains("saveEpub("),
        )

        // Narration persistence, which it must not do. NarrationStore is where a narrated
        // book's text, plan and audio live; writing there would make this a narrated book.
        listOf("NarrationImporter", "NarrationStore", "saveBookText", "StructureParser")
            .forEach { forbidden ->
                assertFalse(
                    "attachEpub now references $forbidden, which would turn attaching an " +
                        "EPUB to an audiobook into a narration import",
                    body.contains(forbidden),
                )
            }
    }

    /**
     * The ebook tab must be gated on the experimental build.
     *
     * Checked against the source because the gate lives inside a private composable, and this
     * project has no Robolectric, so there is no way to render the library screen in a unit test.
     * Without this the gate can be dropped and every test still passes -- verified by doing
     * exactly that.
     *
     * It is the only thing standing between an in-progress feature and every beta tester's
     * library screen.
     */
    @Test
    fun `the ebook library tab is gated on the experimental build`() {
        val source = sourceOf(AUDIOCHOICE_APP)

        assertTrue(
            "the ebook shelf is no longer gated on NarrationConfig.enabled, so the tab row " +
                "would appear in the beta build",
            source.contains("NarrationConfig.enabled && LibraryShelves.hasEbooks("),
        )
        // And the tab row must be rendered behind that flag rather than unconditionally.
        val tabRowIndex = source.indexOf("TabRow(")
        assertTrue("the library tab row was not found", tabRowIndex > 0)
        val guardIndex = source.indexOf("if (ebooksAvailable) {")
        assertTrue(
            "the tab row is not inside the ebooksAvailable guard",
            guardIndex in 1 until tabRowIndex,
        )
    }

    /**
     * The narrated-book path is the only caller of the importer.
     *
     * If the player ever constructs one, the read-along path has gained the ability to
     * create a second library entry for a book the listener already owns.
     */
    @Test
    fun `the player does not construct a narration importer`() {
        val source = sourceOf(PLAYER_VIEW_MODEL)
        assertFalse(
            "PlayerViewModel constructs a NarrationImporter",
            source.contains("NarrationImporter("),
        )
        // Reading narration playback state is expected and fine: the player has to know a
        // narrated book is still rendering. Writing narration artifacts is not.
        assertTrue(
            "PlayerViewModel should still observe narration playback state",
            source.contains("NarrationPlaybackState"),
        )
    }

    // region source access

    private fun sourceOf(relativePath: String): String {
        val file = candidates(relativePath).firstOrNull(File::isFile)
        // Fails loudly rather than passing vacuously if the layout moves. A guard that
        // silently stops reading the file it guards is worse than no guard.
        assertTrue(
            "could not locate $relativePath from ${File("").absolutePath}; this guard " +
                "would otherwise pass without checking anything",
            file != null,
        )
        return file!!.readText()
    }

    /** Unit tests run with the module directory as the working directory, but not always. */
    private fun candidates(relativePath: String): List<File> = listOf(
        File(relativePath),
        File("app/$relativePath"),
        File("../app/$relativePath"),
    )

    /**
     * The text of one function, from its declaration to the closing brace at its own
     * indentation. Crude, and sufficient: these are top-level members of a class, so the
     * closing brace sits at exactly four spaces.
     */
    private fun methodBody(relativePath: String, declaration: String): String {
        val source = sourceOf(relativePath)
        val start = source.indexOf(declaration)
        assertTrue("$declaration was not found in $relativePath", start >= 0)
        val end = source.indexOf("\n    }", start)
        assertTrue("the end of $declaration was not found", end > start)
        return source.substring(start, end)
    }

    // endregion

    private companion object {
        const val AUDIOCHOICE_APP =
            "src/main/java/com/audiochoice/mobile/ui/AudioChoiceApp.kt"
        const val PLAYER_VIEW_MODEL =
            "src/main/java/com/audiochoice/mobile/player/PlayerViewModel.kt"
    }
}
