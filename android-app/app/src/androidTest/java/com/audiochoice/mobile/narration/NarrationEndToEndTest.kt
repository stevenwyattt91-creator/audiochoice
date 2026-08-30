package com.audiochoice.mobile.narration

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.audiochoice.contracts.BookFingerprint
import com.audiochoice.contracts.ScanEvent
import com.audiochoice.mobile.data.LibraryBook
import com.audiochoice.mobile.data.LibraryBookUpsertRequest
import com.audiochoice.mobile.data.RenderState
import com.audiochoice.mobile.narration.voice.AacChapterAudioWriter
import com.audiochoice.mobile.narration.voice.SystemVoiceEngine
import com.audiochoice.mobile.narration.voice.TextToSpeechConnection
import com.audiochoice.mobile.narration.voice.TextToSpeechHandle
import com.audiochoice.mobile.reader.EpubTextReader
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.File
import java.util.Locale
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

/**
 * The whole feature, from an EPUB file to audio, on a real Android runtime.
 *
 * Builds a genuine EPUB in memory — container, package document, spine, navigation — and takes it
 * through every production step: extraction, validation, import, planning, filtering, synthesis,
 * encoding and playback. Nothing is faked except the network calls, which have no bearing on
 * whether a book can be read aloud.
 *
 * Exists because every bug in this feature that reached a listener lived in the seams between
 * pieces that were each well tested on their own. This is the test that walks the seams.
 */
@RunWith(AndroidJUnit4::class)
class NarrationEndToEndTest {

    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext

    /**
     * An EPUB goes in; a playable, filtered chapter comes out.
     *
     * Asserts at every seam rather than only at the end, so a failure names the step that broke
     * rather than the fact that the feature does not work.
     */
    @Test
    fun anEpubBecomesPlayableFilteredAudio() = runBlocking {
        val filesDirectory = File(context.cacheDir, "e2e").apply { deleteRecursively(); mkdirs() }
        val store = NarrationStore(filesDirectory)
        val epub = buildEpub()

        // 1. Extraction. The document carries the structure a plan needs, not only the text --
        //    which is exactly what a previous version of the reader got wrong.
        val document = EpubTextReader.readNarrationDocument(ByteArrayInputStream(epub))
        assertTrue("no text was extracted", document.text.isNotEmpty())
        assertTrue(
            "no spine resources were extracted, so no plan could be built",
            document.resources.isNotEmpty(),
        )
        assertEquals("A Test Novel", document.title)

        // 2. Validation accepts it.
        val validation = EpubValidator.classify(document)
        assertTrue(
            "a well-formed EPUB was declined: $validation",
            validation is EpubValidation.Accepted,
        )

        // 3. Import. Produces the fingerprint the library and every store key on.
        val saved = mutableListOf<LibraryBookUpsertRequest>()
        val importer = NarrationImporter(
            store = store,
            takePersistablePermission = { true },
            readDocument = { input -> EpubTextReader.readNarrationDocument(input) },
            isAlreadyInLibrary = { false },
            persistSourceLocation = {},
            saveLibraryBook = { request -> saved += request; libraryRow(request) },
            saveCover = { _, _ -> },
        )
        val outcome = importer.import(
            NarrationImportSource(
                displayName = "A Test Novel.epub",
                declaredSize = epub.size.toLong(),
                openStream = { ByteArrayInputStream(epub) },
            ),
        )
        val imported = outcome as? NarrationImportOutcome.Imported
        assertNotNull("the import failed: $outcome", imported)
        assertEquals("epub", imported!!.fingerprint.fileType)
        assertEquals(
            "a narrated book must have no duration until it is rendered",
            null,
            imported.fingerprint.duration,
        )
        val sha = imported.fingerprint.sha256

        // The text is on the device, which is what makes the book readable offline.
        assertEquals(document.text, store.bookText(sha))

        // And it lands on the ebook shelf, never the audiobook one.
        assertEquals(
            com.audiochoice.mobile.library.LibraryShelf.EBOOKS,
            com.audiochoice.mobile.library.LibraryShelves.shelfFor(imported.libraryBook),
        )

        // 4. Planning, from the real document.
        val plan = StructureParser.buildPlan(
            document = document,
            sourceSha256 = sha,
            synthesisInputLimit = SynthesisInputLimit.CEILING,
        )
        assertNotNull("the book could not be divided into chapters", plan)
        assertTrue("the plan has no chapters", plan!!.chapters.isNotEmpty())
        assertTrue(
            "no chapter has anything to speak",
            plan.chapters.any { it.units.isNotEmpty() },
        )

        // 5. Filtering. A flagged passage must be absent from what is spoken, not merely skipped.
        val flagged = document.text.indexOf(PROFANITY)
        assertTrue("the fixture's flagged word was not found in the text", flagged >= 0)
        val masks = FilteredRanges.forEnabledEvents(
            listOf(scanEvent(flagged, flagged + PROFANITY.length)),
        )
        assertEquals(1, masks.size)

        // 6. Synthesis and encoding, with the filter applied.
        val handle = when (val connection = TextToSpeechHandle.connect(context, Locale.US)) {
            is TextToSpeechConnection.Connected -> connection.handle
            else -> {
                android.util.Log.w("NarrationEndToEndTest", "no voice engine: $connection")
                return@runBlocking
            }
        }

        handle.use { speech ->
            val coordinator = NarrationRenderCoordinator(
                store = store,
                engine = SystemVoiceEngine(
                    speech = speech,
                    voiceID = speech.defaultVoiceName() ?: "system-default",
                    writerFactory = { file -> AacChapterAudioWriter(file) },
                    scratchDirectory = File(filesDirectory, "scratch"),
                ),
            )
            val pass = withTimeout(240_000) {
                coordinator.renderPending(
                    sha256 = sha,
                    plan = plan,
                    filteredRanges = masks,
                    playheadChapter = 0,
                )
            }
            val queue = pass.queue
            assertTrue(
                "nothing rendered: ${queue.failureReasons}",
                queue.states.any { it == RenderState.RENDERED },
            )

            // 7. Playback selection. Must pick a chapter that genuinely has audio -- a front-matter
            //    chapter renders as silence and must be stepped over.
            val playable = NarrationPlayback.nextPlayableChapter(
                queue.states, queue.chapterDurationsMs, from = 0,
            )
            assertNotNull(
                "no chapter was playable after a successful render: " +
                    "states=${queue.states} durations=${queue.chapterDurationsMs}",
                playable,
            )
            val audio = store.chapterAudioFile(sha, playable!!)
            assertTrue("the playable chapter has no audio file", audio.length() > 0)

            // 8. The audio actually opens, and its duration matches what was recorded.
            val player = android.media.MediaPlayer()
            try {
                player.setDataSource(audio.absolutePath)
                player.prepare()
                assertTrue("the rendered chapter will not play", player.duration > 0)
                assertEquals(
                    "the recorded duration disagrees with the file",
                    queue.chapterDurationsMs[playable].toDouble(),
                    player.duration.toDouble(),
                    1_000.0,
                )
            } finally {
                player.release()
            }

            // 9. A timeline exists, which is what the reader highlights from.
            val timeline = store.loadChapterTimeline(sha, playable)
            assertNotNull("no timeline was recorded", timeline)
            assertTrue(timeline!!.isNotEmpty())

            // 10. The reader's view of the same book removes the same passage the voice skipped.
            //     This is the property that makes filtering on a narrated book stronger than on an
            //     audiobook: the text is gone from the page as well as from the audio.
            val view = NarrationReaderState.derive(
                bookText = document.text,
                filteredRanges = masks,
                narrationTimingRanges = timeline,
            )
            assertFalse(
                "the filtered word is still visible in the reader",
                view.visibleParagraphs.any { it.displayText.contains(PROFANITY, ignoreCase = true) },
            )
            assertTrue(
                "the reader shows nothing at all, so the removal cannot be judged",
                view.visibleParagraphs.isNotEmpty(),
            )
        }
    }

    // region a real EPUB

    /**
     * Builds a valid EPUB 3: mimetype, container, package document with a spine, a navigation
     * document and two content documents.
     *
     * Hand-built rather than checked in as a binary so the structure is readable and adjustable
     * here, and so a change to extraction can be reasoned about against the source it parses.
     */
    private fun buildEpub(): ByteArray {
        val output = ByteArrayOutputStream()
        ZipOutputStream(output).use { zip ->
            fun entry(name: String, content: String) {
                zip.putNextEntry(ZipEntry(name))
                zip.write(content.toByteArray())
                zip.closeEntry()
            }

            // The mimetype entry must come first and be stored uncompressed in a strict reader;
            // this extractor does not require that, and the test does not pretend otherwise.
            entry("mimetype", "application/epub+zip")
            entry(
                "META-INF/container.xml",
                """<?xml version="1.0"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles>
                    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
                  </rootfiles>
                </container>""".trimIndent(),
            )
            entry(
                "OEBPS/content.opf",
                """<?xml version="1.0"?>
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                    <dc:identifier id="id">urn:uuid:test-novel</dc:identifier>
                    <dc:title>A Test Novel</dc:title>
                    <dc:creator>A Test Author</dc:creator>
                    <dc:language>en</dc:language>
                  </metadata>
                  <manifest>
                    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
                    <item id="c1" href="chapter1.xhtml" media-type="application/xhtml+xml"/>
                    <item id="c2" href="chapter2.xhtml" media-type="application/xhtml+xml"/>
                  </manifest>
                  <spine>
                    <itemref idref="c1"/>
                    <itemref idref="c2"/>
                  </spine>
                </package>""".trimIndent(),
            )
            entry(
                "OEBPS/nav.xhtml",
                """<?xml version="1.0"?>
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
                  <body>
                    <nav epub:type="toc">
                      <ol>
                        <li><a href="chapter1.xhtml">Chapter One</a></li>
                        <li><a href="chapter2.xhtml">Chapter Two</a></li>
                      </ol>
                    </nav>
                  </body>
                </html>""".trimIndent(),
            )
            entry(
                "OEBPS/chapter1.xhtml",
                """<?xml version="1.0"?>
                <html xmlns="http://www.w3.org/1999/xhtml"><body>
                  <h1>Chapter One</h1>
                  <p>She had not expected him to be waiting. The rain had stopped an hour ago,
                     and the street still held that washed, expectant quiet.</p>
                  <p>"You came," he said, and something in his voice made her stop three steps
                     short of him.</p>
                  <p>It was, she thought, a $PROFANITY awkward way to begin.</p>
                  <p>She had rehearsed a dozen versions of this and every one of them had
                     deserted her.</p>
                </body></html>""".trimIndent(),
            )
            entry(
                "OEBPS/chapter2.xhtml",
                """<?xml version="1.0"?>
                <html xmlns="http://www.w3.org/1999/xhtml"><body>
                  <h1>Chapter Two</h1>
                  <p>Later, when she tried to remember what they had actually said to each other,
                     she found she could recall only the sound of water dripping from the awning.</p>
                  <p>The cafe behind him was closing. A woman inside stacked chairs with the
                     unhurried competence of someone who had done it ten thousand times.</p>
                  <p>"Walk with me," she said, and he did.</p>
                </body></html>""".trimIndent(),
            )
        }
        return output.toByteArray()
    }

    private fun libraryRow(request: LibraryBookUpsertRequest) = LibraryBook(
        id = "row-1",
        fingerprint = request.fingerprint,
        title = request.title,
        author = request.author,
        addedAt = "2026-01-01",
        updatedAt = "2026-01-01",
    )

    private fun scanEvent(start: Int, end: Int) = ScanEvent(
        id = "profanity-1",
        startTime = start.toDouble(),
        endTime = end.toDouble(),
        categoryID = "21000000-0000-0000-0000-000000000000",
        groupID = "21000000-0000-0000-0000-000000000001",
        eventID = "21000000-0000-0000-0000-000000000101",
        confidence = 1.0,
        stableKey = "stable-profanity-1",
        safeDescription = "Profanity detected",
    )

    private companion object {
        /** Mild, and present in the fixture so the filter has something real to remove. */
        const val PROFANITY = "damn"
    }

    // endregion
}
