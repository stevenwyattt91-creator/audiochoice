package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.SourceRange
import com.audiochoice.mobile.reader.EpubDocument
import com.audiochoice.mobile.reader.NavigationEntry
import com.audiochoice.mobile.reader.NavigationOutline
import com.audiochoice.mobile.reader.NavigationSource
import com.audiochoice.mobile.reader.ResourceSpan
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * Pins what a plan actually needs from a document.
 *
 * Written after a real bug: the reader built an `EpubDocument` out of the stored text alone, with
 * empty resources and no navigation, and handed that to the planner. Every book then reported that
 * it could not be divided into chapters. The text is not the document -- the structure is the rest
 * of it, and without spine spans the planner has no idea where anything is.
 */
class StructurePlanFromDocumentTest {

    // region what a plan needs

    /**
     * A document with text but no resources produces no plan.
     *
     * This is the exact shape the bug produced. Asserted so nobody rebuilds a document from text
     * again and expects it to work: the failure is silent and total, and it looks like a broken
     * book rather than a broken caller.
     */
    @Test
    fun `a document with no resources cannot be planned`() {
        val plan = StructureParser.buildPlan(
            document = document(resources = emptyList()),
            sourceSha256 = SHA,
            synthesisInputLimit = SynthesisInputLimit.CEILING,
        )
        assertNull(
            "a document with no spine spans produced a plan, so this guard no longer describes " +
                "what the planner needs",
            plan,
        )
    }

    @Test
    fun `a document with no text cannot be planned`() {
        assertNull(
            StructureParser.buildPlan(
                document = document(text = ""),
                sourceSha256 = SHA,
                synthesisInputLimit = SynthesisInputLimit.CEILING,
            ),
        )
    }

    /** With resources present, a book with no navigation still plans: the spine is enough. */
    @Test
    fun `a book with resources and no navigation plans from the spine`() {
        val plan = StructureParser.buildPlan(
            document = document(navigation = null),
            sourceSha256 = SHA,
            synthesisInputLimit = SynthesisInputLimit.CEILING,
        )
        assertNotNull("a book with no contents list should still be readable aloud", plan)
        assertTrue(plan!!.chapters.isNotEmpty())
        assertTrue("chapters should carry units to speak", plan.chapters.any { it.units.isNotEmpty() })
    }

    /** And a book with navigation uses it. */
    @Test
    fun `a book with navigation plans from its contents list`() {
        val plan = StructureParser.buildPlan(
            document = document(),
            sourceSha256 = SHA,
            synthesisInputLimit = SynthesisInputLimit.CEILING,
        )
        assertNotNull(plan)
        assertEquals(SHA, plan!!.inputs.sourceSha256)
        assertTrue(plan.chapters.size >= 2)
    }

    // endregion

    // region the reader must not fabricate a document

    /**
     * The read-aloud path has to obtain a real document rather than assemble one.
     *
     * Checked against the source because the mistake is a constructor call with plausible-looking
     * empty defaults, which compiles, passes every other test, and breaks every book at runtime.
     */
    @Test
    fun `the view model does not construct an EpubDocument`() {
        val source = sourceOf(NARRATION_VIEW_MODEL)
        assertTrue(
            "the view model builds its own EpubDocument again, which the planner cannot use",
            !source.contains("EpubDocument("),
        )
        // It must obtain one instead.
        assertTrue(
            "the view model has no way to read the book's real document",
            source.contains("readDocument"),
        )
    }

    /**
     * A re-read that produces different text must be refused rather than planned against.
     *
     * Every offset in the scan and the reader is measured against the stored text. Planning against
     * different text would put chapter boundaries at offsets that do not match the words on screen.
     */
    @Test
    fun `a text length mismatch is refused rather than planned`() {
        val source = sourceOf(NARRATION_VIEW_MODEL)
        assertTrue(
            "nothing checks that a re-read document matches the stored text",
            source.contains("document.text.length != bookText.length"),
        )
    }

    // endregion

    // region fixtures

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

    /**
     * A document shaped like a real two-chapter book: two spine documents that contributed text,
     * and a contents list pointing at each.
     */
    private fun document(
        text: String = CHAPTER_ONE + CHAPTER_TWO,
        resources: List<ResourceSpan> = listOf(
            ResourceSpan("OEBPS/ch1.xhtml", SourceRange(0, CHAPTER_ONE.length)),
            ResourceSpan(
                "OEBPS/ch2.xhtml",
                SourceRange(CHAPTER_ONE.length, CHAPTER_ONE.length + CHAPTER_TWO.length),
            ),
        ),
        navigation: NavigationOutline? = NavigationOutline(
            NavigationSource.EPUB3_NAV,
            listOf(
                NavigationEntry("Chapter One", "OEBPS/ch1.xhtml"),
                NavigationEntry("Chapter Two", "OEBPS/ch2.xhtml"),
            ),
        ),
    ) = EpubDocument(
        text = text,
        extractionVersion = 1,
        language = "en",
        title = "A Novel",
        author = "An Author",
        coverImageEntry = null,
        resources = resources,
        nonProseRanges = emptyList(),
        anchorOffsets = emptyMap(),
        navigation = navigation,
        declaresNavigation = navigation != null,
        encryptedEntries = emptySet(),
        storeDrmResources = emptyList(),
        unreadableSpineEntries = emptyList(),
        declaredSpineEntries = listOf("OEBPS/ch1.xhtml", "OEBPS/ch2.xhtml"),
    )

    private companion object {
        const val SHA = "abc123"
        val CHAPTER_ONE = "Chapter One\n\n" +
            "The quick brown fox jumped over the lazy dog. ".repeat(30)
        val CHAPTER_TWO = "Chapter Two\n\n" +
            "She had not expected him to be waiting there at all. ".repeat(30)
        const val NARRATION_VIEW_MODEL =
            "src/main/java/com/audiochoice/mobile/narration/NarrationViewModel.kt"
    }

    // endregion
}
