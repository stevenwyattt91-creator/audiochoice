package com.audiochoice.mobile.narration.voice

import com.audiochoice.mobile.data.ReaderTimingRange
import com.audiochoice.mobile.data.VoiceKind
import com.audiochoice.mobile.narration.SpokenUnit
import java.io.Closeable
import java.io.File

/**
 * Produces one chapter's audio, and the timings that go with it.
 *
 * The seam is per chapter rather than per unit for one reason worth stating: the
 * timings and the audio have to be built together. A timing says "this passage is at
 * this moment in this file", and the only place that is knowable is where the samples
 * are being appended. Splitting it -- an engine that returns clips, someone else that
 * stitches them -- means measuring durations after the fact, and a millisecond of
 * measurement error per unit accumulates into a highlight that drifts a sentence
 * behind the voice by the end of a chapter.
 */
interface VoiceEngine {
    val kind: VoiceKind
    val voiceID: String

    /**
     * The engine's own input ceiling, which may be lower than the plan's limit.
     *
     * The plan is deliberately independent of the selected voice, so a voice with a
     * tighter ceiling is handled here by splitting what is sent. That changes the
     * request without moving a single character offset.
     */
    val maximumInputCharacters: Int

    suspend fun renderChapter(request: ChapterRenderRequest): ChapterRenderOutcome
}

/**
 * One chapter's work.
 *
 * [units] arrive with filtered characters already removed and pronunciation rules
 * already applied, so an engine never has to know what filtering or pronunciation
 * mean. Each unit still reports the whole original range it came from, which is what
 * keeps the reader and the audio agreeing about which passage is playing.
 */
data class ChapterRenderRequest(
    val bookKey: String,
    val chapterIndex: Int,
    val language: String?,
    val units: List<SpokenUnit>,
    /** Where finished audio belongs. The engine writes a partial file and renames. */
    val destination: File,
)

sealed interface ChapterRenderOutcome {

    /**
     * [timings] are chapter-relative, measured from the first sample of this
     * chapter's own audio, exactly as they will be persisted.
     */
    data class Rendered(
        val audioFile: File,
        val durationMs: Long,
        val timings: List<ReaderTimingRange>,
    ) : ChapterRenderOutcome

    /**
     * [retryable] separates "try again" from "this will never work".
     *
     * The distinction matters because the two look identical at the call site and
     * want opposite handling: a transient synthesis error deserves another attempt,
     * a missing voice does not, and retrying the latter burns the retry budget and
     * delays telling the listener something useful.
     */
    data class Failed(val reason: String, val retryable: Boolean) : ChapterRenderOutcome

    /**
     * Stopped without failing: the listener paused, the worker was cancelled, or the
     * process is going away. Deliberately not a failure, because a failure would
     * consume a retry and eventually mark the chapter unrenderable for something that
     * was never wrong with it.
     */
    data object Cancelled : ChapterRenderOutcome
}

/**
 * The narrow platform seam for turning text into audio.
 *
 * Everything interesting -- retries, timeouts, splitting over-long text, building
 * timings -- lives above this interface so it can be exercised without a device. What
 * remains below is a call into the framework, which a unit test could only ever
 * mock anyway.
 */
interface SpeechSynthesizer {

    /** The most characters one request may carry. */
    val maximumInputCharacters: Int

    /**
     * Synthesize [text] into [destination], returning true on success.
     *
     * Implementations must not perform network requests: the free voices are offered
     * on the promise that a listener's book does not leave their device.
     */
    suspend fun synthesize(text: String, destination: File): Boolean
}

/**
 * Accumulates a chapter's audio into one file, reporting where each piece landed.
 *
 * [append] returns the running total *after* appending, which is how per-unit
 * boundaries are taken from the sample count rather than measured afterwards.
 */
interface ChapterAudioWriter : Closeable {

    /** Appends decoded audio from [source] and returns the running duration. */
    fun append(source: File): Long

    /** Finalises the container and returns the total duration. */
    fun finish(): Long
}
