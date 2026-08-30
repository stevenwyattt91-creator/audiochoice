package com.audiochoice.mobile.narration

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * The library row serves both shelves, so its labels have to suit whichever book is in it.
 *
 * An ebook has no running time and was not imported as an audiobook. Telling someone their own book is
 * an "Imported audiobook" with a duration of "—" is the kind of small wrongness that makes a whole
 * screen feel unreliable, and it was what the shared row did before this.
 *
 * Checked against source: rendering a composable needs Robolectric, which this project does not use.
 */
class EbookLibraryRowTest {

    @Test
    fun `an ebook is not described as an audiobook`() {
        val body = functionBody(APP, "private fun LibraryBookRow(")
        assertTrue(
            "the row no longer distinguishes the two shelves, so an ebook is labelled with " +
                "audiobook wording",
            body.contains("LibraryShelves.shelfFor(book) == LibraryShelf.EBOOKS"),
        )
        assertTrue(
            "an ebook with no author is still described as an imported audiobook",
            body.contains("if (isEbook) \"Imported ebook\" else \"Imported audiobook\""),
        )
    }

    /**
     * An ebook's running time does not exist until a voice has read it.
     *
     * `formatDuration` renders a null duration as a bare dash, so every ebook row showed an em dash
     * where an audiobook shows its length.
     */
    @Test
    fun `an ebook does not show a running time it does not have`() {
        val body = functionBody(APP, "private fun LibraryBookRow(")
        val guarded = body.indexOf("if (isEbook) {")
        val formatted = body.indexOf("formatDuration(book.fingerprint.duration)")
        assertTrue("the shelf is no longer branched on", guarded > 0)
        assertTrue(
            "the duration is formatted outside the audiobook branch, so an ebook shows a dash " +
                "where a length belongs",
            formatted > guarded,
        )
    }

    /**
     * A book that cannot be filtered says so in the list.
     *
     * Finding out only after opening the book and pressing Read aloud wastes the trip, and the
     * decision it presents is one the listener can only make knowingly.
     */
    @Test
    fun `an ebook with no filter results says so in the list`() {
        assertTrue(
            "the row no longer reports missing filter results",
            functionBody(APP, "private fun LibraryBookRow(").contains("filtersUnavailable"),
        )
        assertTrue(
            "the shelf no longer passes the filter state to its rows",
            functionBody(APP, "private fun LibraryHome(")
                .contains("in ebooksWithoutFilterResults"),
        )
    }

    /**
     * Only ebooks are examined, and only those still awaiting a decision are reported.
     *
     * A listener who already chose to continue without results has decided; repeating it reads as
     * nagging. Scanning every audiobook for a file that is never there would be work for nothing.
     */
    @Test
    fun `only ebooks still awaiting a decision are reported`() {
        val body = functionBody(LIBRARY_VIEW_MODEL, "    private fun refreshEbookFilterState(")
        assertTrue(
            "audiobooks are now examined too, which searches every audiobook for a file that is " +
                "never there",
            body.contains("LibraryShelf.EBOOKS"),
        )
        assertTrue(
            "a book whose owner already chose to continue without filter results is reported as " +
                "still awaiting that decision",
            body.contains("continuedWithoutFilterResults"),
        )
        assertTrue(
            "the presence of a stored scan no longer settles the question",
            body.contains("store.textScan(sha) == null"),
        )
    }

    /**
     * Reading the filter state must not be able to empty the library.
     *
     * It runs after the list is published, so a failure or a slow read leaves the books on screen.
     */
    @Test
    fun `the filter state is read after the library is published`() {
        val source = sourceOf(LIBRARY_VIEW_MODEL)
        val published = source.indexOf("mutableState.value = LibraryUiState(\n                    loaded = true,")
        val read = source.indexOf("refreshEbookFilterState(it.first)")
        assertTrue("the library is no longer published there", published > 0)
        assertTrue("the filter state is no longer read", read > 0)
        assertTrue(
            "the filter state is read before the library is published, so a slow or failing read " +
                "would delay or empty the list",
            published < read,
        )
        assertFalse(
            "the filter state read is now inside the block whose failure empties the library",
            functionBody(LIBRARY_VIEW_MODEL, "    private fun refreshEbookFilterState(")
                .contains("LibraryUiState("),
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

    private fun functionBody(relativePath: String, declaration: String): String {
        val source = sourceOf(relativePath)
        val start = source.indexOf(declaration)
        assertTrue("$declaration was not found", start >= 0)
        val end = source.indexOf(if (declaration.startsWith("    ")) "\n    }" else "\n}", start)
        assertTrue("the end of $declaration was not found", end > start)
        return source.substring(start, end)
    }

    private companion object {
        const val APP = "src/main/java/com/audiochoice/mobile/ui/AudioChoiceApp.kt"
        const val LIBRARY_VIEW_MODEL =
            "src/main/java/com/audiochoice/mobile/library/LibraryViewModel.kt"
    }
}
