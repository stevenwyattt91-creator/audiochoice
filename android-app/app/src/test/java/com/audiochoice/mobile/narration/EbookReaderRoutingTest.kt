package com.audiochoice.mobile.narration

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * Guards the routing between the player and the ebook reader.
 *
 * Source-level, because both live in a Compose function and this project has no Robolectric, so
 * there is no way to render the library screen in a unit test. Each assertion below was verified
 * to fail when the thing it guards was removed.
 */
class EbookReaderRoutingTest {

    /**
     * A narrated book must never be handed to the player.
     *
     * `player.open` looks for an audio file this book has none of, and reports the listener's own
     * book as unplayable. There are three places a book reaches the player -- the row tap, the
     * green resume button, and the Player tab picking a book when none is loaded -- and all three
     * have to route or skip.
     */
    @Test
    fun `every path into the player skips narrated books`() {
        val source = sourceOf(AUDIOCHOICE_APP)

        // Row tap and resume button both route ebooks to the reader.
        val routedOpens = Regex("""LibraryShelves\.shelfFor\(book\) == LibraryShelf\.EBOOKS""")
            .findAll(source).count()
        assertTrue(
            "expected the row tap and the resume button to both route ebooks, found $routedOpens",
            routedOpens >= 2,
        )

        // The Player tab's fallback must not pick a narrated book.
        assertTrue(
            "the Player tab can still open the first book in the library, which may be narrated",
            source.contains(
                "firstOrNull { LibraryShelves.shelfFor(it) == LibraryShelf.AUDIOBOOKS }",
            ),
        )
    }

    /**
     * The reader is reached through its own destination rather than through the audiobook detail
     * overlay, so an ebook never lands on a surface built around chapters, bookmarks and a runtime
     * it does not have.
     */
    @Test
    fun `the reader has its own destination`() {
        val source = sourceOf(AUDIOCHOICE_APP)
        assertTrue(
            "the reader destination is missing",
            source.contains("var readerBook by remember") &&
                source.contains("EbookReaderScreen("),
        )
        // Checked before the drawer and scaffold, so it takes precedence over the tab content.
        val readerIndex = source.indexOf("readerBook?.let { book ->")
        val drawerIndex = source.indexOf("ModalNavigationDrawer(")
        assertTrue("the reader destination was not found", readerIndex > 0)
        assertTrue(
            "the reader is rendered after the scaffold, so it would not take precedence",
            readerIndex < drawerIndex,
        )
    }

    /** All of it gated, so the beta build's library behaves exactly as it does today. */
    @Test
    fun `reader routing is gated on the experimental build`() {
        val source = sourceOf(AUDIOCHOICE_APP)
        val gatedRoutes = Regex(
            """NarrationConfig\.enabled &&\s*\n\s*LibraryShelves\.shelfFor\(book\) == LibraryShelf\.EBOOKS""",
        ).findAll(source).count()
        assertTrue(
            "expected both open paths to be gated on the experimental build, found $gatedRoutes",
            gatedRoutes >= 2,
        )
    }

    /**
     * The reader surface must not reach the player.
     *
     * Sharing the reader's typography and masking is the point; sharing a `MediaController` would
     * mean a narrated book could start or stop audiobook playback.
     */
    @Test
    fun `the ebook reader does not touch the player`() {
        val body = functionBody(AUDIOCHOICE_APP, "private fun EbookReaderScreen(")
        listOf("player.", "PlayerViewModel", "PlayerUiState").forEach { forbidden ->
            assertFalse(
                "EbookReaderScreen references $forbidden, so a narrated book could drive " +
                    "audiobook playback",
                body.contains(forbidden),
            )
        }
        // And it does render the shared reader pieces, which is what it is supposed to reuse.
        assertTrue(body.contains("readerPalette("))
        assertTrue(body.contains("readerFontFamily("))
        assertTrue(body.contains("ReaderSettingsDialog("))
    }

    /**
     * The reader's masks come from the same place the renderer's do, so what is hidden on screen
     * and what is never spoken cannot disagree.
     */
    @Test
    fun `the reader builds its masks from FilteredRanges`() {
        val body = functionBody(AUDIOCHOICE_APP, "private fun EbookReaderScreen(")
        assertTrue(
            "the reader no longer uses FilteredRanges, so the text and the audio could disagree " +
                "about what is filtered",
            body.contains("FilteredRanges.forEnabledEvents("),
        )
    }

    // region source access

    private fun sourceOf(relativePath: String): String {
        val file = listOf(
            File(relativePath),
            File("app/$relativePath"),
            File("../app/$relativePath"),
        ).firstOrNull(File::isFile)
        assertTrue(
            "could not locate $relativePath from ${File("").absolutePath}; this guard would " +
                "otherwise pass without checking anything",
            file != null,
        )
        return file!!.readText()
    }

    /** From a declaration to the closing brace at its own indentation. */
    private fun functionBody(relativePath: String, declaration: String): String {
        val source = sourceOf(relativePath)
        val start = source.indexOf(declaration)
        assertTrue("$declaration was not found", start >= 0)
        val end = source.indexOf("\n}", start)
        assertTrue("the end of $declaration was not found", end > start)
        return source.substring(start, end)
    }

    private companion object {
        const val AUDIOCHOICE_APP =
            "src/main/java/com/audiochoice/mobile/ui/AudioChoiceApp.kt"
    }

    // endregion
}
