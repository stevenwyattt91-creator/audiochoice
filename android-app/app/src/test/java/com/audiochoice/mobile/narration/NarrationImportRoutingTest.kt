package com.audiochoice.mobile.narration

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

class NarrationImportRoutingTest {

    // region which files route where

    /**
     * Routing is decided on the file name, not the reported MIME type.
     *
     * The picker has to offer permissive types -- many providers report an EPUB as
     * `application/octet-stream`, and a strict `epub+zip` filter makes the listener's own file
     * unselectable -- so the reported type is frequently the same for an EPUB and an M4B. The name
     * is what the listener chose and what providers are most reliable about.
     */
    @Test
    fun `epub files route to the ebook import`() {
        listOf(
            "Novel.epub", "novel.EPUB", "A Book.ePub",
            "The Silent Patient - Alex Michaelides.epub",
        ).forEach { name ->
            assertTrue("$name should route to the ebook import",
                NarrationImportCoordinator.isEpub(name))
        }
    }

    @Test
    fun `audio files do not route to the ebook import`() {
        listOf(
            "Book.m4b", "Book.mp3", "Book.m4a", "Book.aax", "Book.flac",
            // No extension at all, which some providers hand back.
            "Book", null,
            // A name that merely mentions epub is not an EPUB.
            "how-to-make-an-epub.m4b", "epub", "book.epub.m4b",
        ).forEach { name ->
            assertFalse("$name should not route to the ebook import",
                NarrationImportCoordinator.isEpub(name))
        }
    }

    /**
     * A file whose whole name is the extension still routes to the ebook path.
     *
     * Degenerate, and deliberately permitted. Sending it to the audiobook pipeline instead would
     * fail for certain, whereas the ebook path can at least try to read it -- and the title
     * fallback already handles a book with no usable name by using its hash.
     */
    @Test
    fun `a name that is only an extension still routes to the ebook path`() {
        assertTrue(NarrationImportCoordinator.isEpub(".epub"))
        assertTrue(NarrationImportCoordinator.isEpub("a.epub"))
        // But an empty name is not a file at all.
        assertFalse(NarrationImportCoordinator.isEpub(""))
        assertFalse(NarrationImportCoordinator.isEpub(null))
    }

    // endregion

    // region the picker

    /**
     * The permissive entries are load-bearing, not laziness: without them a listener's own EPUB is
     * greyed out in the file picker on a great many providers.
     */
    @Test
    fun `the narration picker offers epub alongside audio`() {
        val types = NarrationImportCoordinator.PICKER_MIME_TYPES.toList()
        assertTrue(types.contains("application/epub+zip"))
        assertTrue(types.contains("audio/*"))
        assertTrue("providers reporting a generic type would make an EPUB unselectable",
            types.contains("application/octet-stream"))
    }

    /**
     * The beta build's picker must be exactly what ships today. Offering an EPUB there would let a
     * tester pick a file the build cannot import.
     */
    @Test
    fun `the audio-only picker is unchanged from what ships`() {
        assertEquals(
            listOf("audio/*", "application/octet-stream"),
            NarrationImportCoordinator.AUDIO_ONLY_PICKER_MIME_TYPES.toList(),
        )
        assertFalse(
            "the beta picker offers EPUBs, which that build cannot import",
            NarrationImportCoordinator.AUDIO_ONLY_PICKER_MIME_TYPES.any {
                it.contains("epub", ignoreCase = true)
            },
        )
    }

    // endregion

    // region the gate

    /**
     * The routing branch, the picker types and the screen copy are all gated on the experimental
     * build. Checked against the source because they live in a view model and a private composable
     * with no way to render either in a unit test.
     *
     * Without the gate a beta tester's picker would offer EPUBs and the branch would route one
     * into a code path that build has no UI for.
     */
    @Test
    fun `ebook import is gated on the experimental build`() {
        assertFalse(
            "narration is enabled outside the experimental build",
            NarrationConfig.enabled,
        )

        val importViewModel = sourceOf(IMPORT_VIEW_MODEL)
        assertTrue(
            "the ebook routing branch is no longer gated, so a beta build would take it",
            importViewModel.contains("NarrationConfig.enabled &&") &&
                importViewModel.contains("NarrationImportCoordinator.isEpub("),
        )

        val app = sourceOf(AUDIOCHOICE_APP)
        assertTrue(
            "the picker no longer chooses its MIME types by build, so beta would offer EPUBs",
            app.contains("if (NarrationConfig.enabled) {") &&
                app.contains("NarrationImportCoordinator.PICKER_MIME_TYPES") &&
                app.contains("NarrationImportCoordinator.AUDIO_ONLY_PICKER_MIME_TYPES"),
        )
    }

    /**
     * The audiobook import path must not have gained a narration dependency. An EPUB attached to
     * an audiobook for read-along is a different thing from an ebook, and the two pipelines share
     * only the extraction code.
     */
    @Test
    fun `the audiobook import pipeline is untouched`() {
        val source = sourceOf(IMPORT_VIEW_MODEL)

        // The AAX branch and the ordinary audiobook path both still exist.
        assertTrue(source.contains("equals(\"aax\", ignoreCase = true)"))
        assertTrue(source.contains("import(uri, resolver, accessToken)"))

        // And the ebook path writes through the coordinator rather than reaching into the
        // narration store from the middle of the audiobook pipeline.
        assertFalse(
            "ImportViewModel now constructs a NarrationStore directly",
            source.contains("NarrationStore("),
        )
    }

    /**
     * The import screen has to publish the saved row, or the library never reloads.
     *
     * The reload is triggered by `savedBook` changing. An ebook import that set only its own
     * outcome field left the library showing a cached list with no ebook in it -- and the Ebooks
     * tab appears only once there is one, so the book was invisible despite being saved on both
     * the server and the device. Exactly the bug this guards.
     */
    @Test
    fun `a successful ebook import publishes the saved library row`() {
        val source = sourceOf(IMPORT_VIEW_MODEL)
        val importedBranch = source.substringAfter("is com.audiochoice.mobile.narration.NarrationImportOutcome.Imported ->")
            .substringBefore("is com.audiochoice.mobile.narration.NarrationImportOutcome.AlreadyInLibrary")
        assertTrue(
            "the ebook success path does not set savedBook, so the library will not reload and " +
                "the Ebooks tab will not appear",
            importedBranch.contains("savedBook = outcome.libraryBook"),
        )
    }

    /** And the screen reloads on any ebook outcome, so a re-import shows the book too. */
    @Test
    fun `the library reloads on every ebook outcome`() {
        val source = sourceOf(AUDIOCHOICE_APP)
        assertTrue(
            "nothing reloads the library when an ebook import finishes",
            source.contains("LaunchedEffect(importState.ebookOutcome)"),
        )
    }

    // endregion

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

    private companion object {
        const val IMPORT_VIEW_MODEL =
            "src/main/java/com/audiochoice/mobile/importing/ImportViewModel.kt"
        const val AUDIOCHOICE_APP =
            "src/main/java/com/audiochoice/mobile/ui/AudioChoiceApp.kt"
    }

    // endregion
}
