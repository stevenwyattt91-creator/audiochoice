package com.audiochoice.mobile.narration.voice

import com.audiochoice.contracts.BookFingerprint
import com.audiochoice.contracts.NarrationChapterRequest
import com.audiochoice.contracts.NarrationChapterStatus
import com.audiochoice.contracts.NarrationUnitRequest
import com.audiochoice.mobile.data.ReaderTimingRange
import com.audiochoice.mobile.data.VoiceKind
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import java.io.File
import java.util.Base64

/**
 * Reads a chapter aloud using the server-side premium voice.
 *
 * Implements the same [VoiceEngine] contract the on-device voice does, which is the point: the
 * render coordinator, the queue, the timelines and the reader do not know which voice made a
 * chapter. A book can therefore hold audio from both -- which is not hypothetical, it is what a
 * lapsed subscription produces, and it is why provider and voice are recorded per chapter.
 *
 * Submits the chapter, polls until it is finished, and writes the returned audio into the same
 * file the on-device engine would have written. From that point the chapter is indistinguishable
 * from a locally-synthesized one for every purpose.
 *
 * The units handed to [renderChapter] already have filtered characters removed and pronunciation
 * rules applied. That ordering is what makes the privacy claim true rather than aspirational: a
 * passage a listener filtered is not sent, because by the time this class sees the chapter the
 * passage is already gone.
 */
class PremiumVoiceEngine(
    override val voiceID: String,
    private val fingerprint: BookFingerprint,
    private val submit: suspend (NarrationChapterRequest) -> String,
    private val poll: suspend (String) -> NarrationChapterStatus,
    private val saveTimeline: suspend (Int, List<ReaderTimingRange>) -> Unit,
    private val pause: suspend (Long) -> Unit = { delay(it) },
) : VoiceEngine {

    override val kind: VoiceKind = VoiceKind.PREMIUM

    /**
     * The server's own ceiling per chapter.
     *
     * Higher than a device engine's per-utterance limit because the server splits the chapter
     * itself, one request per unit. The plan is unaffected either way: a voice with a tighter
     * ceiling changes what is sent, never a character offset.
     */
    override val maximumInputCharacters: Int = MAXIMUM_CHAPTER_CHARACTERS

    override suspend fun renderChapter(request: ChapterRenderRequest): ChapterRenderOutcome {
        if (request.units.isEmpty()) {
            // A chapter whose every unit was filtered away. Nothing is sent, nothing is billed,
            // and it counts as rendered because there is nothing left to produce.
            return ChapterRenderOutcome.Rendered(
                audioFile = request.destination,
                durationMs = 0L,
                timings = emptyList(),
            )
        }

        val characters = request.units.sumOf { it.text.length }
        if (characters > MAXIMUM_CHAPTER_CHARACTERS) {
            // Refused here rather than sent and rejected, so the listener is not billed for a
            // request that cannot succeed.
            return ChapterRenderOutcome.Failed(
                "This chapter is too long for the premium voice. Try an on-device voice.",
                // Never retryable: the chapter will be exactly as long next time.
                retryable = false,
            )
        }

        val jobID = try {
            submit(
                NarrationChapterRequest(
                    fingerprint = fingerprint,
                    chapterIndex = request.chapterIndex,
                    voiceID = voiceID,
                    language = request.language,
                    units = request.units.map { unit ->
                        NarrationUnitRequest(unit.startCharacter, unit.endCharacter, unit.text)
                    },
                ),
            )
        } catch (cancellation: CancellationException) {
            throw cancellation
        } catch (failure: Throwable) {
            // Retryable: a submission that did not land is a connection problem, and the chapter
            // itself is fine.
            return ChapterRenderOutcome.Failed(
                failure.message ?: "The premium voice could not be reached.",
                retryable = true,
            )
        }

        val finished = awaitCompletion(jobID) ?: return ChapterRenderOutcome.Failed(
            "The premium voice did not finish this chapter in time.",
            // The work may well have completed after the deadline, so another attempt is
            // reasonable rather than futile.
            retryable = true,
        )
        if (finished.isFailed) {
            return ChapterRenderOutcome.Failed(
                finished.error ?: "The premium voice could not make this chapter.",
                // The server distinguishes these itself; from here a reported failure is worth one
                // more attempt, and the coordinator's retry budget bounds it.
                retryable = true,
            )
        }

        val audio = finished.audioBase64?.let { encoded ->
            runCatching { Base64.getDecoder().decode(encoded) }.getOrNull()
        }
        if (audio == null || audio.isEmpty()) {
            return ChapterRenderOutcome.Failed(
                "The premium voice returned no audio.",
                retryable = true,
            )
        }

        // Written atomically. A reader that observed a half-downloaded file would treat a truncated
        // chapter as the whole chapter, and the listener would hear it stop mid-sentence with
        // nothing to indicate why.
        val written = runCatching {
            val partial = File(request.destination.absolutePath + PARTIAL_SUFFIX)
            partial.parentFile?.mkdirs()
            partial.writeBytes(audio)
            partial.renameTo(request.destination) || run {
                request.destination.delete()
                partial.renameTo(request.destination)
            }
        }.getOrDefault(false)
        if (!written) {
            // Not retryable: a write that failed will fail again, and the useful answer is that
            // the device has no room rather than a fourth attempt.
            return ChapterRenderOutcome.Failed(
                "This chapter's audio could not be saved. Check your available storage.",
                retryable = false,
            )
        }

        val timings = finished.timings.map { timing ->
            ReaderTimingRange(
                startCharacter = timing.startCharacter,
                endCharacter = timing.endCharacter,
                startTime = timing.startSeconds,
                endTime = timing.endSeconds,
            )
        }
        saveTimeline(request.chapterIndex, timings)

        return ChapterRenderOutcome.Rendered(
            audioFile = request.destination,
            durationMs = (finished.durationSeconds * 1_000).toLong(),
            timings = timings,
        )
    }

    /**
     * Polls until the job finishes, or gives up.
     *
     * Backs off from half a second to four, because a chapter takes tens of seconds and polling
     * every half second for all of it would be a hundred wasted requests. Returns null on the
     * overall deadline rather than throwing, so the coordinator records a failure and steps on
     * instead of taking the whole book down.
     */
    private suspend fun awaitCompletion(jobID: String): NarrationChapterStatus? {
        var waited = 0L
        var interval = INITIAL_POLL_MS
        while (waited < MAXIMUM_WAIT_MS) {
            val status = try {
                poll(jobID)
            } catch (cancellation: CancellationException) {
                throw cancellation
            } catch (_: Throwable) {
                // A dropped poll is not a failed chapter: the work continues on the server, and
                // the next poll will find it. Only the overall deadline ends this.
                null
            }
            if (status != null && (status.isCompleted || status.isFailed)) return status

            pause(interval)
            waited += interval
            interval = (interval * 2).coerceAtMost(MAXIMUM_POLL_MS)
        }
        return null
    }

    companion object {
        /** Matches the server's own per-chapter bound. */
        const val MAXIMUM_CHAPTER_CHARACTERS = 40_000

        const val INITIAL_POLL_MS = 500L
        const val MAXIMUM_POLL_MS = 4_000L

        /**
         * How long one chapter may take before the listener is told it failed.
         *
         * Five minutes is far longer than a chapter should need, and the point is not to be
         * generous but to be finite: a chapter that never resolves would leave a book saying it is
         * being prepared for ever, which is the failure that looks like patience being required.
         */
        const val MAXIMUM_WAIT_MS = 5L * 60 * 1_000

        private const val PARTIAL_SUFFIX = ".partial"
    }
}
