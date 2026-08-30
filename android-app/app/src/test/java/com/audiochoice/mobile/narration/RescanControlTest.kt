package com.audiochoice.mobile.narration

import com.audiochoice.mobile.player.FilterAvailability
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * A book with no filter data must offer a way to get some.
 *
 * The player says filters are not active, which is the right thing to say — a book with no scan plays
 * exactly like a book with nothing to filter, so without the warning the two are indistinguishable.
 * But until this existed the warning was a dead end: it named the problem and offered nothing, and the
 * only remedy was deleting the book and importing it again, which nothing on screen said.
 *
 * A book reaches that state without doing anything wrong. An edition nobody has scanned has nothing to
 * inherit, and the edition matcher correctly refuses to lend one recording's filters to another. A
 * tester hit exactly this on a reissue of a book whose earlier edition scanned fine.
 */
class RescanControlTest {

    /** The control is offered where the warning is shown. */
    @Test
    fun `the player offers a rescan beside the inactive-filters warning`() {
        val body = playerScreen()
        assertTrue(
            "the player no longer offers a rescan, so the warning is a dead end again",
            body.contains("onRescan"),
        )
        // Inside the branch that shows the warning, not somewhere it would always appear.
        val warning = body.indexOf("Filters are not active.")
        val button = body.indexOf("Scan this audiobook")
        assertTrue("the warning is gone", warning > 0)
        assertTrue("the control is not beside the warning", button > warning)
    }

    /**
     * Offered only when the file is on the device.
     *
     * Scanning needs the audio. Offering it for a book whose file has gone would fail after the
     * listener had already committed to waiting.
     */
    @Test
    fun `the control is withheld when there is no local file`() {
        val app = source(APP)
        assertTrue(
            "the control no longer depends on a local file being present",
            app.contains("onRescan = playerState.localUri?.let"),
        )
    }

    /**
     * Starting a scan must take the listener where progress is shown.
     *
     * Import progress lives on the import tab. A scan the listener cannot watch looks like a button
     * that did nothing, which is how they end up pressing it repeatedly.
     */
    @Test
    fun `starting a rescan moves to where progress is visible`() {
        val app = source(APP)
        val start = app.indexOf("importer.rescan(uri, context.contentResolver, accessToken)")
        assertTrue("the rescan no longer starts a scan", start > 0)
        assertTrue(
            "the listener is left on the player with no sign anything is happening",
            app.indexOf("selected = 2", start) in (start + 1)..(start + 200),
        )
    }

    /**
     * The rescan reuses the ordinary import path.
     *
     * The library row is an upsert keyed by the file's fingerprint, so repeating it re-scans the book
     * rather than creating a second one. Reimplementing the flow would mean a second upload, polling
     * and recovery path to keep in step with the first.
     */
    @Test
    fun `the rescan reuses the import path rather than a parallel one`() {
        val body = functionBody(IMPORT_VIEW_MODEL, "    fun rescan(")
        assertTrue(
            "rescan no longer delegates to import, so the upload, polling and recovery behaviour " +
                "would have to be maintained twice",
            body.contains("import(uri, resolver, accessToken)"),
        )
    }

    /**
     * A failed rescan must leave the book alone.
     *
     * On iOS the equivalent screen discards an incomplete import, which would have deleted a book the
     * listener already had along with its audio file. Android's import has no such step, and this
     * records that: adding one would silently turn a failed scan into a lost book.
     */
    @Test
    fun `a failed import does not delete the book`() {
        val importer = source(IMPORT_VIEW_MODEL)
        listOf("deleteBook(", "api.deleteBook", "localAudio.remove(").forEach { destructive ->
            assertFalse(
                "the import path now removes a book on failure, so a failed rescan would delete a " +
                    "book the listener already had: found '$destructive'",
                importer.contains(destructive),
            )
        }
    }

    /** The warning itself still appears, since silent inactive filtering is the worse failure. */
    @Test
    fun `the warning is still shown when filters are unavailable`() {
        assertTrue(
            "the warning no longer keys off unavailable filter data",
            playerScreen().contains("state.filterAvailability == FilterAvailability.UNAVAILABLE"),
        )
        // Guards the case the warning and the control both key off. LIVE and CACHED both mean
        // filters are enforced -- one fetched this session, one restored from a saved scan -- so
        // UNAVAILABLE is the only state with nothing to enforce and the only one worth offering a
        // scan from.
        assertTrue(
            "the filter states changed, so the warning may no longer cover every case with " +
                "nothing being enforced",
            FilterAvailability.entries.map { it.name }
                .containsAll(listOf("LOADING", "LIVE", "CACHED", "UNAVAILABLE")),
        )
    }

    private fun playerScreen(): String = functionBody(APP, "private fun PlayerScreen(")

    private fun source(relativePath: String): String {
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
        val text = source(relativePath)
        val start = text.indexOf(declaration)
        assertTrue("$declaration was not found", start >= 0)
        val terminator = if (declaration.startsWith("    ")) "\n    }\n" else "\n}\n"
        val end = text.indexOf(terminator, start)
        assertTrue("the end of $declaration was not found", end > start)
        return text.substring(start, end)
    }

    private companion object {
        const val APP = "src/main/java/com/audiochoice/mobile/ui/AudioChoiceApp.kt"
        const val IMPORT_VIEW_MODEL =
            "src/main/java/com/audiochoice/mobile/importing/ImportViewModel.kt"
    }
}
