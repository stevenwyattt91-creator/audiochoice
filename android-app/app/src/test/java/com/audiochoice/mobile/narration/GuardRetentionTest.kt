package com.audiochoice.mobile.narration

import com.audiochoice.contracts.BookFingerprint
import com.audiochoice.mobile.data.FilterReportComposer
import com.audiochoice.mobile.data.FilterReportRequest
import com.audiochoice.mobile.data.NarrationFlags
import com.audiochoice.mobile.data.VoiceKind
import com.audiochoice.mobile.narration.voice.OnDeviceRate
import com.audiochoice.mobile.narration.voice.PremiumVoiceAgreement
import com.audiochoice.mobile.narration.voice.PremiumVoiceGate
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * Every client-side promise in this feature that is one plausible edit away from being lost.
 *
 * Collected in one place because they are otherwise scattered across the files they protect, and a
 * reviewer asking "what must stay true?" has nowhere to look. Each assertion here was verified to
 * fail when the thing it guards was removed — several exist only because I made that edit, found
 * nothing caught it, and wrote the guard afterwards.
 *
 * The source-level checks are deliberate rather than lazy. What matters in those cases is which
 * types a function reaches for and whether a condition is present, and a compiled method body does
 * not expose either. There is no Robolectric here, so a composable or a view model cannot be
 * exercised in a JVM test at all.
 */
class GuardRetentionTest {

    // region nothing reaches a build that cannot handle it

    /**
     * Every narration surface is gated on the experimental build.
     *
     * This is the only thing between an in-progress feature and every beta tester's library screen.
     */
    @Test
    fun `narration is off outside the experimental build`() {
        assertFalse(
            "narration is enabled in a non-experimental build",
            NarrationConfig.enabled,
        )
    }

    @Test
    fun `every narration entry point checks the build flag`() {
        val gates = mapOf(
            "the ebook library tab" to (AUDIOCHOICE_APP to "NarrationConfig.enabled && LibraryShelves.hasEbooks("),
            "the picker's MIME types" to (AUDIOCHOICE_APP to "NarrationImportCoordinator.AUDIO_ONLY_PICKER_MIME_TYPES"),
            "the ebook import branch" to (IMPORT_VIEW_MODEL to "NarrationConfig.enabled &&"),
        )
        gates.forEach { (what, where) ->
            val (path, expected) = where
            assertTrue(
                "$what is no longer gated on the experimental build",
                sourceOf(path).contains(expected),
            )
        }
    }

    // endregion

    // region the shipping audiobook path stays untouched

    /**
     * Attaching an EPUB to an audiobook is read-along and must never create a narrated book.
     *
     * The two are one plausible refactor apart: `attachEpub` already holds an EPUB URI and already
     * extracts its text, so routing it into the importer looks like deduplication. It would
     * silently synthesise audio for books that already have a narrator, and bill for it.
     */
    @Test
    fun `attaching an EPUB to an audiobook does not create a narrated book`() {
        val body = functionBody(PLAYER_VIEW_MODEL, "fun attachEpub")
        listOf("NarrationImporter", "NarrationStore", "saveBookText").forEach { forbidden ->
            assertFalse(
                "attachEpub now references $forbidden",
                body.contains(forbidden),
            )
        }
    }

    /**
     * A narrated book must never be handed to the audiobook player, which would look for audio it
     * has none of and report the listener's own book as unplayable.
     */
    @Test
    fun `the player is never handed a narrated book`() {
        val source = sourceOf(AUDIOCHOICE_APP)
        assertTrue(
            "the Player tab can open the first library book, which may be narrated",
            source.contains("firstOrNull { LibraryShelves.shelfFor(it) == LibraryShelf.AUDIOBOOKS }"),
        )
        assertTrue(
            "the row tap and resume button no longer route ebooks to the reader",
            Regex("""LibraryShelves\.shelfFor\(book\) == LibraryShelf\.EBOOKS""")
                .findAll(source).count() >= 2,
        )
    }

    /**
     * An existing client's filter report must serialise byte-identically.
     *
     * The app configures its `Json` with `encodeDefaults = true`, so "optional field with a null
     * default" is *not* automatically wire-compatible — without the annotation a null is still
     * written out. That is the whole reason the annotation is load-bearing.
     */
    @Test
    fun `an audiobook filter report carries no position unit`() {
        val body = Json { encodeDefaults = true }.encodeToString(
            FilterReportRequest.serializer(),
            FilterReportComposer.missedContent(
                fingerprint = FINGERPRINT.copy(fileType = "m4b"),
                positionSeconds = 1.0,
                scannerVersion = "v1",
            ),
        )
        assertFalse(
            "positionUnit is now written for an audiobook report, changing the wire shape",
            body.contains("positionUnit"),
        )
    }

    /** Narration audio must stay outside the orphan-file purge, which would delete every chapter. */
    @Test
    fun `narration audio is not swept by the orphan purge`() {
        val declaration = sourceOf(LOCAL_AUDIO_STORE)
            .substringAfter("PURGEABLE_AUDIO_DIRECTORIES")
            .substringAfter("listOf(")
            .substringBefore(")")
        val directories = declaration.split(",").map { it.trim().trim('"') }.filter { it.isNotEmpty() }
        assertTrue("the purge list parsed as empty", directories.isNotEmpty())
        assertFalse(
            "narration/ is swept by the orphan purge",
            directories.any { it.contains(NarrationConfig.ROOT_DIRECTORY, ignoreCase = true) },
        )
    }

    // endregion

    // region nothing leaves the device without permission

    /**
     * The render path gates the premium voice on the agreement.
     *
     * The most serious guard here. Dropping the `maySubmit` conjunction reads like removing a
     * redundant check and would send a listener's book off the device with no acceptance recorded.
     * Nothing else in the suite caught it, which was verified by making the edit.
     */
    @Test
    fun `premium synthesis is gated on the recorded agreement`() {
        val source = sourceOf(NARRATION_VIEW_MODEL)
        assertTrue(
            "the render path no longer consults the agreement gate",
            source.contains("PremiumVoiceAgreement.maySubmit("),
        )
        val selection = source.substringAfter("val usePremium =").substringBefore("val engine =")
        assertTrue("usePremium no longer depends on the gate", selection.contains("maySubmit"))
    }

    @Test
    fun `only an allowed gate permits submission`() {
        assertEquals(
            listOf(true, false, false, false),
            listOf(
                PremiumVoiceGate.Allowed,
                PremiumVoiceGate.NotEntitled,
                PremiumVoiceGate.AgreementRequired("1", "text"),
                PremiumVoiceGate.AgreementChanged("1", "2", "text"),
            ).map(PremiumVoiceAgreement::maySubmit),
        )
    }

    /** Only the premium voice sends anything anywhere, and every free voice must keep text local. */
    @Test
    fun `every free-tier voice keeps the book on the device`() {
        NarrationTiers.availableVoiceKinds(NarrationTier.FREE, localNeuralSupported = true)
            .forEach { kind ->
                assertFalse(
                    "$kind is offered free but sends text off the device",
                    NarrationTiers.sendsTextOffDevice(kind),
                )
            }
        assertTrue(NarrationTiers.sendsTextOffDevice(VoiceKind.PREMIUM))
    }

    /** A book's text must not be printable by accident. */
    @Test
    fun `the scan request prints its length rather than its text`() {
        val request = com.audiochoice.contracts.NarrationTextScanRequest(
            fingerprint = FINGERPRINT,
            bookText = "A marker phrase ZQXMARKER inside the book.",
            language = "en",
        )
        assertFalse(
            "the request's rendering exposes the book's text",
            request.toString().contains("ZQXMARKER"),
        )
        assertTrue(request.toString().contains("bookTextCharacters"))
    }

    @Test
    fun `the chapter request prints its size rather than its text`() {
        val request = com.audiochoice.contracts.NarrationChapterRequest(
            fingerprint = FINGERPRINT,
            chapterIndex = 0,
            voiceID = "Ruth",
            units = listOf(
                com.audiochoice.contracts.NarrationUnitRequest(0, 30, "ZQXMARKER in a chapter."),
            ),
        )
        assertFalse(
            "the chapter request's rendering exposes the text to be spoken",
            request.toString().contains("ZQXMARKER"),
        )
    }

    // endregion

    // region filtering is honoured before anything is spoken

    /**
     * Rendering may not begin before filter results are settled.
     *
     * Audio, once written, is what the listener hears until it is re-made, so speaking before
     * filters are known would deliver the passages they asked to remove and the removal would
     * arrive too late to matter.
     */
    @Test
    fun `nothing is rendered before filter results are settled`() {
        assertFalse(
            NarrationUiState(readiness = NarrationReadiness.AWAITING_FILTERS).mayRender,
        )
        assertFalse(NarrationUiState(readiness = NarrationReadiness.UNREADABLE).mayRender)
        assertFalse(NarrationUiState(readiness = NarrationReadiness.LOADING).mayRender)
        // Only an explicit, recorded choice unblocks it.
        assertTrue(
            NarrationUiState(
                readiness = NarrationReadiness.AWAITING_FILTERS,
                flags = NarrationFlags(continuedWithoutFilterResults = true),
            ).mayRender,
        )
    }

    /**
     * The reader's masks and the renderer's exclusions come from one place, so what is hidden on
     * screen and what is never spoken cannot disagree.
     */
    @Test
    fun `the reader and the renderer share one source of filtered ranges`() {
        assertTrue(
            "the reader no longer builds its masks from FilteredRanges",
            functionBody(AUDIOCHOICE_APP, "private fun EbookReaderScreen(")
                .contains("FilteredRanges.forEnabledEvents("),
        )
    }

    /** A whole batch is discarded on one bad offset: half a filter is worse than none. */
    @Test
    fun `one out-of-range offset invalidates a whole scan`() {
        val events = listOf(
            eventAt(0, 10),
            eventAt(20, 30),
            eventAt(90, 500),
        )
        assertFalse(FilteredRanges.offsetsAreValid(events, bookTextLength = 100))
        assertTrue(FilteredRanges.offsetsAreValid(events.dropLast(1), bookTextLength = 100))
    }

    // endregion

    // region playback cannot lie about what exists

    /**
     * A rendered chapter with no audio is not playable.
     *
     * A title page renders correctly as nothing. Conflating "rendered" with "has audio" selected
     * it, found no file, and failed silently — which reached a listener as a button that did
     * nothing.
     */
    @Test
    fun `a rendered but silent chapter is never selected to play`() {
        val states = List(3) { com.audiochoice.mobile.data.RenderState.RENDERED }
        assertEquals(
            2,
            NarrationPlayback.nextPlayableChapter(states, listOf(0L, 0L, 90_000L), from = 0),
        )
        assertNull(
            NarrationPlayback.nextPlayableChapter(states, listOf(0L, 0L, 0L), from = 0),
        )
    }

    /** Book_Time counts only audio that exists, or every later position would drift. */
    @Test
    fun `an unrendered chapter contributes no time`() {
        assertEquals(
            120.0,
            NarrationPlayback.chapterOffsetSeconds(
                listOf(60_000L, 60_000L, 60_000L, 60_000L),
                listOf(
                    com.audiochoice.mobile.data.RenderState.RENDERED,
                    com.audiochoice.mobile.data.RenderState.NOT_RENDERED,
                    com.audiochoice.mobile.data.RenderState.RENDERED,
                    com.audiochoice.mobile.data.RenderState.RENDERED,
                ),
                chapterIndex = 3,
            ),
            0.001,
        )
    }

    /** A plan needs the book's structure, not only its text. */
    @Test
    fun `the reader obtains a real document rather than building one`() {
        val source = sourceOf(NARRATION_VIEW_MODEL)
        assertFalse(
            "the view model constructs its own EpubDocument, which the planner cannot use",
            source.contains("EpubDocument("),
        )
        assertTrue(source.contains("readDocument"))
    }

    // endregion

    // region the storage reserve is actually consulted

    /**
     * The render path must read real free space, not leave the check inert.
     *
     * `NarrationRenderCoordinator` defaults its free-space reader to `{ null }` so it can be
     * constructed in a test without a filesystem — which means a caller that forgets to supply one
     * gets a check that silently never fires. That default is convenient and dangerous in equal
     * measure, so the production call site is pinned.
     */
    @Test
    fun `the render path supplies a real free-space reading`() {
        val source = sourceOf(NARRATION_VIEW_MODEL)
        assertTrue(
            "the coordinator is constructed without a free-space reader, so the storage reserve " +
                "would never stop a render and the device could be filled",
            source.contains("freeBytes ="),
        )
        assertTrue(
            "free space is not read from the directory the audio is written to",
            source.contains("store.bookDirectory(sha256).usableSpace"),
        )
    }

    /** And the listener is told, in terms they can act on. */
    @Test
    fun `running out of storage is reported rather than silent`() {
        assertTrue(
            "an out-of-storage stop produces no message",
            sourceOf(NARRATION_VIEW_MODEL).contains("StopReason.OUT_OF_STORAGE"),
        )
    }

    // endregion

    // region measured values stay measured

    /**
     * Three constants here were derived from plausible assumptions and each was 13 to 33 percent
     * wrong, always over-estimating. These guard the figures that replaced them.
     */
    @Test
    fun `the speech rates remain the measured ones`() {
        assertEquals(18.4, NarrationStorage.charactersPerSecond(VoiceKind.SYSTEM), 0.5)
        assertEquals(18.0, NarrationStorage.charactersPerSecond(VoiceKind.PREMIUM), 0.5)
        assertTrue(NarrationStorage.rateIsMeasured(VoiceKind.SYSTEM))
        assertTrue(NarrationStorage.rateIsMeasured(VoiceKind.PREMIUM))
        assertFalse(
            "LOCAL_NEURAL has not been measured and must not claim to be",
            NarrationStorage.rateIsMeasured(VoiceKind.LOCAL_NEURAL),
        )
    }

    @Test
    fun `the benchmark passage stays comparable with the recorded measurement`() {
        assertEquals(515, OnDeviceRate.BENCHMARK_CHARACTERS)
        assertEquals(3.0, OnDeviceRate.MINIMUM_REAL_TIME_FACTOR, 0.0)
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

    private fun functionBody(relativePath: String, declaration: String): String {
        val source = sourceOf(relativePath)
        val start = source.indexOf(declaration)
        assertTrue("$declaration was not found in $relativePath", start >= 0)
        val end = source.indexOf("\n    }", start).takeIf { it > start }
            ?: source.indexOf("\n}", start)
        assertTrue("the end of $declaration was not found", end > start)
        return source.substring(start, end)
    }

    private fun eventAt(start: Int, end: Int) = com.audiochoice.contracts.ScanEvent(
        id = "$start-$end",
        startTime = start.toDouble(),
        endTime = end.toDouble(),
        categoryID = "21000000-0000-0000-0000-000000000000",
        groupID = "21000000-0000-0000-0000-000000000001",
        eventID = "21000000-0000-0000-0000-000000000101",
        confidence = 1.0,
        stableKey = "stable-$start",
        safeDescription = "Something occurs",
    )

    private companion object {
        const val AUDIOCHOICE_APP = "src/main/java/com/audiochoice/mobile/ui/AudioChoiceApp.kt"
        const val PLAYER_VIEW_MODEL = "src/main/java/com/audiochoice/mobile/player/PlayerViewModel.kt"
        const val IMPORT_VIEW_MODEL = "src/main/java/com/audiochoice/mobile/importing/ImportViewModel.kt"
        const val NARRATION_VIEW_MODEL = "src/main/java/com/audiochoice/mobile/narration/NarrationViewModel.kt"
        const val LOCAL_AUDIO_STORE = "src/main/java/com/audiochoice/mobile/data/LocalAudioStore.kt"

        val FINGERPRINT = BookFingerprint(
            version = 1,
            sha256 = "a".repeat(64),
            fileSize = 1_024,
            duration = null,
            fileType = "epub",
        )
    }

    // endregion
}
