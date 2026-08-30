package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.NarrationPlan
import com.audiochoice.mobile.data.RenderQueue
import com.audiochoice.mobile.data.RenderState
import com.audiochoice.mobile.narration.voice.ChapterRenderOutcome
import com.audiochoice.mobile.narration.voice.ChapterRenderRequest
import com.audiochoice.mobile.narration.voice.VoiceEngine
import com.audiochoice.mobile.reader.ReaderMask
import kotlinx.coroutines.CancellationException

/**
 * Runs the render loop: pick a chapter, produce it, record what happened, decide
 * again.
 *
 * A loop of single-chapter steps rather than one long job. That shape is what makes a
 * chapter boundary always a safe place to stop, so a killed process, a cancelled
 * worker or a listener who paused all resume at a chapter rather than halfway through
 * one. Encoder state cannot be resumed, so any interrupted chapter has to start over
 * regardless; making the step small keeps the amount of lost work small.
 *
 * The `WorkManager` worker on top of this is a shell. Everything worth reasoning about
 * lives here, so it can be exercised with a fake voice and a real store instead of
 * needing a device.
 */
class NarrationRenderCoordinator(
    private val store: NarrationStore,
    private val engine: VoiceEngine,
    private val window: RenderAheadWindow = RenderAheadWindow.DEFAULT,
    /** Applied to spoken text only, never to Book_Text. */
    private val pronounce: (String) -> String = { it },
    private val onProgress: (NarrationProgress) -> Unit = {},
    /**
     * Free bytes on the volume the audio is written to, or null where it cannot be read.
     *
     * A function rather than a value because a render takes minutes and the renderer is not the only
     * thing consuming space while it runs: a download, a photo or another app can take the reserve
     * mid-chapter. Asked before each chapter for that reason.
     *
     * Null means "cannot tell", and cannot-tell must not stop a render: a device that will not
     * report its free space is not a device that is full, and refusing on that basis would make the
     * feature unusable for no benefit.
     */
    private val freeBytes: () -> Long? = { null },
) {

    /**
     * Bring a book up to the render-ahead window, or as far as it can get.
     *
     * Returns the queue as persisted, so the caller never has to reconstruct it from
     * what it thinks happened.
     */
    suspend fun renderPending(
        sha256: String,
        plan: NarrationPlan,
        filteredRanges: List<ReaderMask>,
        playheadChapter: Int = 0,
        fullBookRequested: Boolean = false,
        pausedByListener: Boolean = false,
    ): RenderPass {
        // Checked before anything is written. Starting a chapter with the reserve already gone would
        // fill the device and then abandon a partial file, which is the worst of both.
        freeBytes()?.let { available ->
            if (NarrationStorage.mustStopRendering(available)) {
                return RenderPass(
                    queue = reconcile(store.loadQueue(sha256) ?: RenderQueue.forPlan(plan), plan),
                    stopReason = StopReason.OUT_OF_STORAGE,
                )
            }
        }
        var queue = store.loadQueue(sha256) ?: RenderQueue.forPlan(plan)
        queue = reconcile(queue, plan)
        queue = sweepInterruptedChapters(sha256, queue)

        if (queue.states.isEmpty()) {
            return RenderPass(queue, StopReason.NOTHING_LEFT)
        }

        publish(plan, queue, renderingIndex = null)

        while (true) {
            val next = NarrationRenderScheduler.nextChapterToRender(
                states = queue.states,
                playheadChapter = playheadChapter,
                renderAheadWindow = window.chapters,
                fullBookRequested = fullBookRequested,
                pausedByListener = pausedByListener,
            ) ?: break

            queue = persist(sha256, queue.withState(next, RenderState.RENDERING))
            publish(plan, queue, renderingIndex = next)

            val outcome = renderChapter(sha256, plan, filteredRanges, next)

            when (outcome) {
                is ChapterOutcome.Rendered -> {
                    store.saveChapterTimeline(sha256, next, outcome.timings)
                    queue = persist(
                        sha256,
                        queue.copy(
                            states = queue.states.toMutableList().also { it[next] = RenderState.RENDERED },
                            chapterDurationsMs = queue.chapterDurationsMs.toMutableList()
                                .also { it[next] = outcome.durationMs },
                            omittedUnitCounts = queue.omittedUnitCounts.toMutableList()
                                .also { it[next] = outcome.omittedUnits },
                            partiallyRemovedUnitCounts = queue.partiallyRemovedUnitCounts.toMutableList()
                                .also { it[next] = outcome.partiallyRemovedUnits },
                            failureReasons = queue.failureReasons - next,
                        ),
                    )
                }

                is ChapterOutcome.Failed -> {
                    // Failed is not not-rendered. The scheduler steps past it rather
                    // than retrying forever, and it returns to the queue only when the
                    // listener asks, so one bad chapter cannot stall a whole book.
                    queue = persist(
                        sha256,
                        queue.copy(
                            states = queue.states.toMutableList()
                                .also { it[next] = RenderState.RENDER_FAILED },
                            failureReasons = queue.failureReasons + (next to outcome.reason),
                        ),
                    )
                }

                ChapterOutcome.Cancelled -> {
                    // Back to not-rendered, so resuming picks it up again. Nothing is
                    // recorded as failed, because nothing was wrong with it.
                    queue = persist(sha256, queue.withState(next, RenderState.NOT_RENDERED))
                    publish(plan, queue, renderingIndex = null)
                    return RenderPass(queue, StopReason.CANCELLED)
                }
            }

            publish(plan, queue, renderingIndex = null)
        }

        val reason = when {
            pausedByListener -> StopReason.PAUSED
            NarrationRenderScheduler.isStalledByFailures(queue.states) -> StopReason.ALL_FAILED
            NarrationRenderScheduler.hasWorkRemaining(queue.states) -> StopReason.WINDOW_SATISFIED
            else -> StopReason.NOTHING_LEFT
        }
        return RenderPass(queue, reason)
    }

    /**
     * Return a failed chapter to the queue at the listener's request.
     *
     * Only a failed chapter, and only its own state: a retry must not disturb audio
     * that already rendered.
     */
    suspend fun retryChapter(sha256: String, chapterIndex: Int): RenderQueue? {
        val queue = store.loadQueue(sha256) ?: return null
        if (queue.states.getOrNull(chapterIndex) != RenderState.RENDER_FAILED) return queue
        return persist(
            sha256,
            queue.copy(
                states = queue.states.toMutableList().also { it[chapterIndex] = RenderState.NOT_RENDERED },
                failureReasons = queue.failureReasons - chapterIndex,
            ),
        )
    }

    /** Return every failed chapter to the queue, for the all-failed case. */
    suspend fun retryAllFailedChapters(sha256: String): RenderQueue? {
        val queue = store.loadQueue(sha256) ?: return null
        val states = queue.states.map { if (it == RenderState.RENDER_FAILED) RenderState.NOT_RENDERED else it }
        return persist(sha256, queue.copy(states = states, failureReasons = emptyMap()))
    }

    private suspend fun renderChapter(
        sha256: String,
        plan: NarrationPlan,
        filteredRanges: List<ReaderMask>,
        chapterIndex: Int,
    ): ChapterOutcome {
        val chapter = plan.chapters.getOrNull(chapterIndex)
            ?: return ChapterOutcome.Failed("Chapter $chapterIndex is not in the plan")

        val speech = SpokenTextBuilder.build(chapter.units, filteredRanges)

        if (speech.isSilent) {
            // Every unit filtered out, or a chapter with no prose. It writes no audio
            // and counts as rendered, because there is nothing left to produce for it.
            store.deleteChapterAudio(sha256, chapterIndex)
            return ChapterOutcome.Rendered(
                durationMs = 0L,
                timings = emptyList(),
                omittedUnits = speech.omittedUnits,
                partiallyRemovedUnits = speech.partiallyRemovedUnits,
            )
        }

        // Pronunciation rules change what is spoken, never what the offsets mean, so
        // they are applied here and the ranges are carried through untouched.
        val units = speech.spoken.map { unit -> unit.copy(text = pronounce(unit.text)) }

        val outcome = try {
            engine.renderChapter(
                ChapterRenderRequest(
                    bookKey = sha256,
                    chapterIndex = chapterIndex,
                    language = null,
                    units = units,
                    destination = store.chapterAudioFile(sha256, chapterIndex),
                ),
            )
        } catch (cancellation: CancellationException) {
            throw cancellation
        } catch (error: Throwable) {
            return ChapterOutcome.Failed(error.message ?: "Chapter render failed")
        }

        return when (outcome) {
            is ChapterRenderOutcome.Rendered -> ChapterOutcome.Rendered(
                durationMs = outcome.durationMs,
                timings = outcome.timings,
                omittedUnits = speech.omittedUnits,
                partiallyRemovedUnits = speech.partiallyRemovedUnits,
            )

            is ChapterRenderOutcome.Failed -> ChapterOutcome.Failed(outcome.reason)
            ChapterRenderOutcome.Cancelled -> ChapterOutcome.Cancelled
        }
    }

    /**
     * Delete leftover partial audio and return those chapters to the queue.
     *
     * A partial file is evidence that a render died mid-chapter, which a clean
     * cancellation path cannot cover because process death does not run it. Sweeping at
     * the start of every pass is what makes the guarantee hold across a killed process
     * rather than only across an orderly stop.
     */
    private suspend fun sweepInterruptedChapters(sha256: String, queue: RenderQueue): RenderQueue {
        val swept = store.sweepPartialAudio(sha256)
        // A chapter left in RENDERING by a process that died is also interrupted, even
        // if its partial file never reached disk.
        val stuck = queue.states.indices.filter { queue.states[it] == RenderState.RENDERING }
        val toReset = (swept + stuck).distinct().filter { it in queue.states.indices }
        if (toReset.isEmpty()) return queue

        val states = queue.states.toMutableList()
        toReset.forEach { states[it] = RenderState.NOT_RENDERED }
        return persist(sha256, queue.copy(states = states))
    }

    /**
     * Resize a persisted queue to the current plan.
     *
     * A plan can be rebuilt with a different chapter count -- a new extraction version, a
     * navigation document that now parses -- and a queue from the old shape would index
     * chapters that no longer exist.
     */
    private fun reconcile(queue: RenderQueue, plan: NarrationPlan): RenderQueue {
        if (queue.states.size == plan.chapters.size) return queue
        return RenderQueue.forPlan(plan)
    }

    private suspend fun persist(sha256: String, queue: RenderQueue): RenderQueue {
        store.saveQueue(sha256, queue)
        return queue
    }

    private fun publish(plan: NarrationPlan, queue: RenderQueue, renderingIndex: Int?) {
        onProgress(
            NarrationProgress(
                renderedChapters = queue.renderedCount,
                failedChapters = queue.failedCount,
                totalChapters = plan.chapters.size,
                renderingChapterTitle = renderingIndex?.let { plan.chapters.getOrNull(it)?.title },
                renderedDurationMs = queue.renderedDurationMs,
            ),
        )
    }

    private sealed interface ChapterOutcome {
        data class Rendered(
            val durationMs: Long,
            val timings: List<com.audiochoice.mobile.data.ReaderTimingRange>,
            val omittedUnits: Int,
            val partiallyRemovedUnits: Int,
        ) : ChapterOutcome

        data class Failed(val reason: String) : ChapterOutcome
        data object Cancelled : ChapterOutcome
    }
}

/** What one render pass achieved, and why it stopped. */
data class RenderPass(val queue: RenderQueue, val stopReason: StopReason)

enum class StopReason {
    /** Enough chapters are ready ahead of the listener; more remain. */
    WINDOW_SATISFIED,

    /**
     * Free space reached the storage reserve.
     *
     * Deliberately not a failure: nothing is wrong with the book or the voice, and the chapters
     * already made are kept along with the queue, so freeing space and asking again continues from
     * where it stopped rather than starting over. Marking it failed would consume a retry budget for
     * something no retry can fix.
     */
    OUT_OF_STORAGE,

    /** Every chapter is rendered. */
    NOTHING_LEFT,

    /** The listener asked rendering to stop. */
    PAUSED,

    /** The worker or the process was going away. */
    CANCELLED,

    /**
     * Everything was attempted and something failed with nothing left to try. The book
     * cannot be finished with this voice, so the listener is offered a retry or a
     * different voice rather than watching a queue that will never move.
     */
    ALL_FAILED,
}

/**
 * What to show while a book is being produced.
 *
 * [renderedDurationMs] is included because it is the book's duration as far as the
 * player is concerned, and it changes every time a chapter lands.
 */
data class NarrationProgress(
    val renderedChapters: Int,
    val failedChapters: Int,
    val totalChapters: Int,
    val renderingChapterTitle: String?,
    val renderedDurationMs: Long,
) {
    val isComplete: Boolean get() = renderedChapters == totalChapters && totalChapters > 0
    val remainingChapters: Int get() = (totalChapters - renderedChapters - failedChapters).coerceAtLeast(0)
}
