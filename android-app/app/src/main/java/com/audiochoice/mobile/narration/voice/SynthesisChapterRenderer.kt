package com.audiochoice.mobile.narration.voice

import com.audiochoice.mobile.data.ReaderTimingRange
import com.audiochoice.mobile.narration.SpokenUnit
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.withTimeout
import java.io.File

/**
 * Drives a [SpeechSynthesizer] over one chapter and assembles the result.
 *
 * This holds everything about rendering that is worth testing: how an over-long unit
 * is split, how many times a failed request is retried, how long a request is given,
 * and how per-unit timings are derived. All of it is platform-free, so it runs in a
 * plain unit test with a fake synthesizer instead of needing a device and a real
 * voice.
 *
 * Shared by every on-device voice. The system voice and an on-device neural voice
 * differ only in how they turn text into samples, not in any of the above, so the
 * policy lives once.
 */
class SynthesisChapterRenderer(
    private val synthesizer: SpeechSynthesizer,
    private val writerFactory: (File) -> ChapterAudioWriter,
    private val scratchFile: (Int) -> File,
    private val requestTimeoutMs: Long = REQUEST_TIMEOUT_MS,
    private val retryDelayMs: Long = 0L,
) {

    suspend fun render(request: ChapterRenderRequest): ChapterRenderOutcome {
        if (request.units.isEmpty()) {
            // A chapter whose every unit was filtered out. No file, no timings, and a
            // zero duration, which is what lets it count as rendered rather than
            // sitting in the queue forever.
            return ChapterRenderOutcome.Rendered(request.destination, 0L, emptyList())
        }

        val partial = File(request.destination.parentFile, request.destination.name + PARTIAL_SUFFIX)
        partial.parentFile?.mkdirs()
        partial.delete()

        val timings = mutableListOf<ReaderTimingRange>()
        var writer: ChapterAudioWriter? = null
        var scratchIndex = 0

        try {
            writer = writerFactory(partial)
            var elapsedMs = 0L

            for (unit in request.units) {
                val unitStartMs = elapsedMs
                // One unit may need several requests when it exceeds the engine's
                // ceiling, but it still gets exactly one timing entry. The reader
                // highlights a sentence, not a fragment of one.
                for (chunk in splitToCeiling(unit.text, synthesizer.maximumInputCharacters)) {
                    val scratch = scratchFile(scratchIndex++)
                    when (val attempt = synthesizeWithRetries(chunk, scratch)) {
                        is AttemptResult.Success -> elapsedMs = writer.append(scratch)
                        is AttemptResult.Failed -> return ChapterRenderOutcome.Failed(
                            reason = attempt.reason,
                            retryable = attempt.retryable,
                        )
                        AttemptResult.Cancelled -> return ChapterRenderOutcome.Cancelled
                    }
                    scratch.delete()
                }

                if (elapsedMs > unitStartMs) {
                    timings += ReaderTimingRange(
                        startTime = unitStartMs / 1_000.0,
                        endTime = elapsedMs / 1_000.0,
                        startCharacter = unit.startCharacter,
                        endCharacter = unit.endCharacter,
                    )
                }
            }

            val total = writer.finish()
            writer = null

            // Rename only once the file is complete, so a reader never observes a
            // half-written chapter. A leftover partial is evidence of a killed render
            // and is swept on the next start.
            if (!partial.renameTo(request.destination)) {
                request.destination.delete()
                if (!partial.renameTo(request.destination)) {
                    return ChapterRenderOutcome.Failed("Could not store chapter audio", true)
                }
            }
            return ChapterRenderOutcome.Rendered(request.destination, total, timings)
        } catch (cancellation: CancellationException) {
            throw cancellation
        } catch (error: Throwable) {
            return ChapterRenderOutcome.Failed(error.message ?: "Chapter render failed", true)
        } finally {
            runCatching { writer?.close() }
            if (partial.exists()) partial.delete()
        }
    }

    private sealed interface AttemptResult {
        data object Success : AttemptResult
        data class Failed(val reason: String, val retryable: Boolean) : AttemptResult
        data object Cancelled : AttemptResult
    }

    /**
     * One request, with a timeout, retried up to [MAXIMUM_RETRIES] times.
     *
     * A request that hangs is treated the same as one that fails, because from the
     * listener's side they are the same thing: a chapter that is not arriving. Without
     * the timeout a stuck engine would hold the queue indefinitely and never surface a
     * reason.
     */
    private suspend fun synthesizeWithRetries(text: String, destination: File): AttemptResult {
        var lastReason = "Synthesis produced no audio"
        repeat(MAXIMUM_RETRIES + 1) { attempt ->
            if (attempt > 0 && retryDelayMs > 0) delay(retryDelayMs)
            destination.delete()

            val succeeded = try {
                withTimeout(requestTimeoutMs) { synthesizer.synthesize(text, destination) }
            } catch (timeout: TimeoutCancellationException) {
                lastReason = "Synthesis timed out after ${requestTimeoutMs}ms"
                false
            } catch (cancellation: CancellationException) {
                // The listener paused or the worker was cancelled. Not a failure, so
                // it must not consume the retry budget.
                return AttemptResult.Cancelled
            } catch (error: Throwable) {
                lastReason = error.message ?: "Synthesis failed"
                false
            }

            if (succeeded && destination.isFile && destination.length() > 0) {
                return AttemptResult.Success
            }
            if (succeeded) lastReason = "Synthesis reported success but wrote no audio"
        }
        return AttemptResult.Failed(lastReason, retryable = false)
    }

    companion object {
        const val REQUEST_TIMEOUT_MS = 30_000L

        /** Three attempts in total, which is the retry budget the requirements set. */
        const val MAXIMUM_RETRIES = 2

        private const val PARTIAL_SUFFIX = ".partial"

        /**
         * Split text to fit an engine's ceiling, at word boundaries.
         *
         * Checked here rather than at plan time because a pronunciation rule can make
         * text longer: replacing a name with a phonetic spelling routinely adds
         * characters, and a unit that fitted when planned may not fit when spoken.
         */
        internal fun splitToCeiling(text: String, ceiling: Int): List<String> {
            if (ceiling <= 0 || text.length <= ceiling) return listOf(text)

            val pieces = mutableListOf<String>()
            var cursor = 0
            while (cursor < text.length) {
                if (text.length - cursor <= ceiling) {
                    pieces += text.substring(cursor)
                    break
                }
                var cut = cursor + ceiling
                while (cut > cursor && !text[cut].isWhitespace()) cut--
                // A single token longer than the ceiling has no boundary to use. Cut
                // it rather than send a request the engine will reject.
                if (cut <= cursor) cut = cursor + ceiling
                pieces += text.substring(cursor, cut).trim()
                cursor = cut
                while (cursor < text.length && text[cursor].isWhitespace()) cursor++
            }
            return pieces.filter { it.isNotEmpty() }
        }
    }
}
