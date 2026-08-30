package com.audiochoice.mobile.narration

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.audiochoice.mobile.data.NarrationChapter
import com.audiochoice.mobile.data.NarrationPlan
import com.audiochoice.mobile.data.NarrationUnit
import com.audiochoice.mobile.data.PlanInputs
import com.audiochoice.mobile.data.RenderState
import com.audiochoice.mobile.narration.voice.AacChapterAudioWriter
import com.audiochoice.mobile.narration.voice.SystemVoiceEngine
import com.audiochoice.mobile.narration.voice.TextToSpeechConnection
import com.audiochoice.mobile.narration.voice.TextToSpeechHandle
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.util.Locale

/**
 * Drives the real render pipeline on a real Android runtime.
 *
 * This exists because of a pattern worth naming: every bug in this feature that reached a listener
 * was in the seam between well-tested pieces, and none of them could be caught by a JVM unit test.
 * The synthesis engine needs `TextToSpeech`, the encoder needs `MediaCodec` and `MediaMuxer`, and
 * the player needs `MediaPlayer` -- all of which are stubs that throw on the JVM. So the whole
 * chain from a plan to a playable file had never executed anywhere except on one tester's phone,
 * and each fault came back as "it does nothing".
 *
 * Everything here uses the production classes. Nothing is faked.
 */
@RunWith(AndroidJUnit4::class)
class NarrationRenderPipelineTest {

    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext

    /**
     * The encoder must produce a playable file and a truthful duration.
     *
     * Written first because the encoder was where a real hang lived: the end-of-stream marker was
     * queued once on a call that is allowed to fail, and when it did the drain loop waited for a
     * flag that could never arrive. That is invisible to every unit test and looks, to a listener,
     * exactly like patience being required.
     */
    @Test
    fun theEncoderProducesPlayableAudioFromSynthesizedSpeech() = runBlocking {
        val scratch = File(context.cacheDir, "render-test").apply { deleteRecursively(); mkdirs() }
        val handle = connectOrSkip() ?: return@runBlocking

        handle.use { speech ->
            val wav = File(scratch, "unit.wav")
            val spoken = withTimeout(30_000) {
                speech.synthesize("The quick brown fox jumped over the lazy dog.", wav)
            }
            assertTrue("the device's voice engine produced no audio", spoken)
            assertTrue("the engine wrote an empty file", wav.length() > 0)

            val destination = File(scratch, "chapter.m4a")
            val duration = AacChapterAudioWriter(destination).use { writer ->
                writer.append(wav)
                writer.finish()
            }

            assertTrue("the encoder reported no duration", duration > 0)
            assertTrue("the encoder wrote no file", destination.length() > 0)

            // Playable is the property that matters. A file that exists but will not open is the
            // failure mode that reached a listener as silence.
            val player = android.media.MediaPlayer()
            try {
                player.setDataSource(destination.absolutePath)
                player.prepare()
                assertTrue("the encoded chapter has no playable duration", player.duration > 0)
                // The encoder's own count and the decoder's agree, which is what makes the reader's
                // highlight land on the right words.
                assertEquals(
                    "the reported duration disagrees with the file",
                    duration.toDouble(),
                    player.duration.toDouble(),
                    750.0,
                )
            } finally {
                player.release()
            }
        }
    }

    /**
     * A chapter that ends on a full input queue must still finish.
     *
     * Appends enough separate utterances to keep the codec busy at the moment `finish()` is called,
     * which is the condition the dropped end-of-stream marker needed. Bounded by a timeout so a
     * regression fails the test rather than hanging the suite -- the whole point being that the
     * original bug hung.
     */
    @Test
    fun aChapterWithManyUnitsFinishesRatherThanHanging() = runBlocking {
        val scratch = File(context.cacheDir, "render-many").apply { deleteRecursively(); mkdirs() }
        val handle = connectOrSkip() ?: return@runBlocking

        handle.use { speech ->
            val wavs = (0 until 6).map { index ->
                File(scratch, "unit-$index.wav").also { file ->
                    withTimeout(30_000) {
                        speech.synthesize(
                            "Passage number $index, long enough to fill an encoder buffer or two " +
                                "with ordinary narrated prose.",
                            file,
                        )
                    }
                }
            }.filter { it.length() > 0 }
            assertTrue("no audio was produced to encode", wavs.isNotEmpty())

            val destination = File(scratch, "chapter.m4a")
            val duration = withTimeout(120_000) {
                AacChapterAudioWriter(destination).use { writer ->
                    wavs.forEach(writer::append)
                    writer.finish()
                }
            }

            assertTrue(duration > 0)
            assertTrue(destination.length() > 0)
        }
    }

    /**
     * The whole chain: a plan goes in, a playable chapter and a queue come out.
     *
     * This is the path the reader's "Read aloud" button takes, exercised end to end with the
     * production coordinator, engine, encoder and store.
     */
    @Test
    fun theCoordinatorRendersAChapterEndToEnd() = runBlocking {
        val filesDirectory = File(context.cacheDir, "render-e2e").apply {
            deleteRecursively()
            mkdirs()
        }
        val store = NarrationStore(filesDirectory)
        val handle = connectOrSkip() ?: return@runBlocking

        handle.use { speech ->
            val engine = SystemVoiceEngine(
                speech = speech,
                voiceID = speech.defaultVoiceName() ?: "system-default",
                writerFactory = { file -> AacChapterAudioWriter(file) },
                scratchDirectory = File(filesDirectory, "scratch"),
            )
            val coordinator = NarrationRenderCoordinator(store = store, engine = engine)

            val pass = withTimeout(180_000) {
                coordinator.renderPending(
                    sha256 = SHA,
                    plan = plan(),
                    filteredRanges = emptyList(),
                    playheadChapter = 0,
                )
            }

            val queue = pass.queue
            assertEquals(
                "the chapter did not render: ${queue.failureReasons}",
                RenderState.RENDERED,
                queue.states.first(),
            )
            assertTrue(
                "the rendered chapter reported no duration",
                queue.chapterDurationsMs.first() > 0,
            )

            // And what the reader would select to play is actually playable.
            val playable = NarrationPlayback.nextPlayableChapter(
                queue.states, queue.chapterDurationsMs, from = 0,
            )
            assertNotNull("nothing was playable after a successful render", playable)
            val audio = store.chapterAudioFile(SHA, playable!!)
            assertTrue("the playable chapter has no audio file", audio.length() > 0)

            // A timeline was recorded, which is what the reader highlights from.
            val timeline = store.loadChapterTimeline(SHA, playable)
            assertNotNull("no timeline was written for a rendered chapter", timeline)
            assertTrue("the timeline is empty", timeline!!.isNotEmpty())
        }
    }

    /**
     * A chapter whose every unit is filtered away renders as silence, and is not offered to play.
     *
     * The other half of a real bug: such a chapter is correctly marked rendered and writes no file,
     * and treating "rendered" as "has audio" then selected it, found nothing, and failed silently.
     */
    @Test
    fun aFullyFilteredChapterRendersAsSilenceAndIsNotPlayable() = runBlocking {
        val filesDirectory = File(context.cacheDir, "render-silent").apply {
            deleteRecursively()
            mkdirs()
        }
        val store = NarrationStore(filesDirectory)
        val handle = connectOrSkip() ?: return@runBlocking

        handle.use { speech ->
            val engine = SystemVoiceEngine(
                speech = speech,
                voiceID = speech.defaultVoiceName() ?: "system-default",
                writerFactory = { file -> AacChapterAudioWriter(file) },
                scratchDirectory = File(filesDirectory, "scratch"),
            )
            val coordinator = NarrationRenderCoordinator(store = store, engine = engine)
            val built = plan()

            val pass = withTimeout(120_000) {
                coordinator.renderPending(
                    sha256 = SHA,
                    plan = built,
                    // Covers the whole book, so nothing survives to be spoken.
                    filteredRanges = listOf(
                        com.audiochoice.mobile.reader.ReaderMask(0, built.chapters.last().endCharacter),
                    ),
                    playheadChapter = 0,
                )
            }

            assertEquals(RenderState.RENDERED, pass.queue.states.first())
            assertEquals(
                "a fully filtered chapter should have no duration",
                0L,
                pass.queue.chapterDurationsMs.first(),
            )
            assertEquals(
                "a silent chapter must not be offered as playable",
                null,
                NarrationPlayback.nextPlayableChapter(
                    pass.queue.states, pass.queue.chapterDurationsMs, from = 0,
                ),
            )
        }
    }

    // region fixtures

    /**
     * Connects to the device's engine, or returns null when there is none.
     *
     * Returning null rather than failing: an emulator image without a voice is a fact about the
     * environment, and a test that fails for that reason teaches nobody anything. The tests that
     * matter here fail loudly when an engine *is* present and the pipeline is broken.
     */
    private suspend fun connectOrSkip(): TextToSpeechHandle? =
        when (val connection = TextToSpeechHandle.connect(context, Locale.US)) {
            is TextToSpeechConnection.Connected -> connection.handle
            else -> {
                android.util.Log.w(
                    "NarrationRenderPipelineTest",
                    "No usable text-to-speech engine on this device: $connection",
                )
                null
            }
        }

    private fun plan(): NarrationPlan {
        val text = CHAPTER_TEXT
        val units = listOf(
            NarrationUnit(0, 46, text.substring(0, 46)),
            NarrationUnit(46, text.length, text.substring(46)),
        )
        return NarrationPlan(
            planVersion = NarrationPlan.PLAN_VERSION,
            inputs = PlanInputs(
                sourceSha256 = SHA,
                bookTextHash = "hash",
                extractionVersion = 1,
                planVersion = NarrationPlan.PLAN_VERSION,
                synthesisInputLimit = SynthesisInputLimit.CEILING,
            ),
            chapterDerivationFellBackToSpine = false,
            chapters = listOf(
                NarrationChapter(
                    index = 0,
                    title = "Chapter One",
                    startCharacter = 0,
                    endCharacter = text.length,
                    units = units,
                ),
            ),
        )
    }

    private companion object {
        const val SHA = "aaaaaaaabbbbbbbbccccccccdddddddd"
        const val CHAPTER_TEXT =
            "The quick brown fox jumped over the lazy dog. " +
                "She had not expected him to be waiting there at all."
    }

    // endregion
}
