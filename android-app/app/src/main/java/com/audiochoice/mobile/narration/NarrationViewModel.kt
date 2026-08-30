package com.audiochoice.mobile.narration

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import com.audiochoice.contracts.ScanEvent
import com.audiochoice.mobile.data.AudioChoiceApi
import com.audiochoice.mobile.data.LibraryBook
import com.audiochoice.mobile.data.LocalAudioStore
import com.audiochoice.mobile.data.NarrationFlags
import com.audiochoice.mobile.data.NarrationPlan
import com.audiochoice.mobile.data.RenderQueue
import com.audiochoice.mobile.data.PronunciationRule
import com.audiochoice.mobile.data.RenderState
import com.audiochoice.mobile.reader.readerCharacterForTime
import com.audiochoice.mobile.reader.readerTimeForCharacter
import com.audiochoice.mobile.data.SelectedVoice
import com.audiochoice.mobile.data.VoiceKind
import com.audiochoice.mobile.reader.ReaderSettings
import com.audiochoice.mobile.reader.ReaderPosition
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.launch
import java.io.File

/** Why a narrated book cannot be read yet. */
enum class NarrationReadiness {
    /** Still loading from disk. */
    LOADING,

    /** Text and filter results are present; the book can be read. */
    READY,

    /**
     * The book's text is on the device but its filter results are not.
     *
     * Reading is allowed and rendering is not, because synthesising before filters are known
     * would speak passages the listener asked to have removed. The listener may choose to
     * continue without filter results, which is recorded and which unblocks rendering.
     */
    AWAITING_FILTERS,

    /** The stored text is gone or unreadable. The book has to be re-imported. */
    UNREADABLE,
}

/**
 * Everything the ebook reader needs for one book.
 *
 * Deliberately separate from `PlayerUiState` rather than an extension of it. The two surfaces
 * share the reader's rendering code and nothing else: this one has no `MediaController`, no
 * chapter marks read from a file, and no audio at all until a voice makes some. Folding a
 * narrated book into the player's state would have meant a nullable field for every audiobook
 * concept and a live one for every narration concept, on a state class the shipping player
 * already reads in forty places.
 */
/** A filter change awaiting the listener's decision, with what it would cost. */
data class PendingFilterChange(
    val choices: FilterChoices,
    val impact: FilterChangeImpact.Rerender,
)

data class NarrationUiState(
    val book: LibraryBook? = null,
    val readiness: NarrationReadiness = NarrationReadiness.LOADING,
    val bookText: String? = null,
    val plan: NarrationPlan? = null,
    val queue: RenderQueue? = null,
    val scanEvents: List<ScanEvent> = emptyList(),
    val scannerVersion: String? = null,
    /** Null until an entitlement read succeeds or a stored one is found. */
    val tier: NarrationTierState? = null,
    val selectedVoice: SelectedVoice? = null,
    val flags: NarrationFlags = NarrationFlags(),
    val readerSettings: ReaderSettings = ReaderSettings(),
    val readerPosition: ReaderPosition = ReaderPosition(),
    val disabledCategoryIDs: Set<String> = emptySet(),
    val disabledGroupIDs: Set<String> = emptySet(),
    val disabledEventKeys: Set<String> = emptySet(),
    val disabledAggregateKeys: Set<String> = emptySet(),
    /** Book_Time, once there is audio to have a position in. */
    val positionSeconds: Double = 0.0,
    val isSpeaking: Boolean = false,
    /** Voices the server offers, with the agreement premium requires. Empty until read. */
    val premiumVoices: List<com.audiochoice.contracts.NarrationVoiceDescriptor> = emptyList(),
    val agreementVersion: String? = null,
    val agreementText: String? = null,
    val agreementRecord: com.audiochoice.mobile.narration.voice.PremiumAgreementRecord? = null,
    /**
     * A filter change that would invalidate rendered audio, held until the listener decides.
     *
     * Deliberately not written through while this is set. Audio once written is what the listener
     * hears, so a change that contradicts it has to be either applied properly or not at all --
     * writing the choice and leaving the audio alone produces a book whose filters and sound
     * disagree, which is the one outcome no later action can explain to them.
     */
    val pendingFilterChange: PendingFilterChange? = null,
    /** Bytes this book's audio occupies, refreshed after anything is written or deleted. */
    val audioBytes: Long = 0L,
    /**
     * How words in this book should be said, book rules before account rules.
     *
     * Applied to spoken text only, never to Book_Text: the reader shows the book as written, and a
     * rule that changed the text on screen would be editing someone's book rather than narrating it.
     */
    val pronunciationRules: List<ScopedRule> = emptyList(),
    /** Why the last attempted rule was refused, cleared as soon as the form changes. */
    val pronunciationRejection: RuleRejection? = null,
    /**
     * Counts rules actually accepted, so the form knows when to clear itself.
     *
     * A counter rather than a boolean: two rules accepted in a row have to be distinguishable, and a
     * boolean that is already true the second time would leave the fields populated.
     *
     * Needed because recording is asynchronous. Clearing the fields on the button press would throw
     * away what someone typed whenever the rule turns out to be refused, which is exactly when they
     * need it back.
     */
    val pronunciationAccepted: Int = 0,
    /**
     * A recorded rule's effect on audio already made, awaiting the listener's decision.
     *
     * The rule itself is already saved by this point. It governs everything rendered from now on
     * whatever they decide here; the only question is whether to redo what already exists.
     */
    val pendingPronunciationRerender: RerenderImpact? = null,
    /**
     * A discard-all awaiting confirmation, with what it reclaims and what it costs.
     *
     * Held rather than acted on because reclaiming space is instant and undoing it is not: the audio
     * comes back only by waiting for it to be made again.
     */
    val pendingDiscardAll: DiscardEstimate? = null,
    val message: String? = null,
    val error: String? = null,
) {
    /** The four choice sets as one value, so a change can be compared against what preceded it. */
    val filterChoices: FilterChoices
        get() = FilterChoices(
            disabledCategoryIDs = disabledCategoryIDs,
            disabledGroupIDs = disabledGroupIDs,
            disabledEventKeys = disabledEventKeys,
            disabledAggregateKeys = disabledAggregateKeys,
        )

    val renderedChapters: Int get() = queue?.renderedCount ?: 0
    val totalChapters: Int get() = plan?.chapters?.size ?: 0
    val failedChapters: Int get() = queue?.failedCount ?: 0
    val isFullyRendered: Boolean
        get() = totalChapters > 0 && renderedChapters == totalChapters

    /**
     * Whether a voice may be asked to speak anything.
     *
     * Filter results have to be settled first: rendering before they are known would speak
     * passages the listener asked to have removed, and audio once written is what the listener
     * hears until it is re-rendered.
     */
    val mayRender: Boolean
        get() = readiness == NarrationReadiness.READY ||
            (readiness == NarrationReadiness.AWAITING_FILTERS && flags.continuedWithoutFilterResults)

    /**
     * Whether the premium voice may be used, and what to do if not.
     *
     * Derived rather than stored, so it cannot go stale against the tier or the agreement it
     * depends on. This is the single check the render path consults before any text leaves the
     * device.
     */
    val premiumGate: com.audiochoice.mobile.narration.voice.PremiumVoiceGate
        get() = com.audiochoice.mobile.narration.voice.PremiumVoiceAgreement.gate(
            isEntitled = tier?.allowsPremiumVoice == true,
            serverVersion = agreementVersion,
            serverText = agreementText,
            recorded = agreementRecord,
        )
}

/**
 * Drives the ebook reader.
 *
 * Loads from disk first and asks the network for as little as possible: a narrated book's text,
 * plan, timings and audio are all local, so a listener with no signal can still read and listen
 * to a book that has been rendered.
 */
class NarrationViewModel(
    private val api: AudioChoiceApi,
    private val localAudio: LocalAudioStore,
    filesDirectory: File,
    /**
     * Re-reads the book's own EPUB, for the structure a plan needs.
     *
     * A function rather than a `ContentResolver` so the view model stays testable, and a
     * re-read rather than a stored copy because the structure is large and the file is already
     * guaranteed readable: the import takes a persistable read permission precisely so it stays
     * that way.
     *
     * The first version of this built an `EpubDocument` from the stored text alone, with empty
     * resources and no navigation. `StructureParser.deriveChapters` returns nothing for a
     * document with no resources -- correctly, since without spine spans it has no idea where
     * anything is -- so every book reported that it could not be divided into chapters. The text
     * is not the document; the structure is the rest of it.
     */
    private val readDocument: suspend (String) -> com.audiochoice.mobile.reader.EpubDocument? =
        { null },
) : ViewModel() {

    private val store = NarrationStore(filesDirectory)
    private val mutableState = MutableStateFlow(NarrationUiState())
    val state: StateFlow<NarrationUiState> = mutableState.asStateFlow()
    private var token: String? = null

    /** Exposed so the reader can build masks and control trees from the same store. */
    val narrationStore: NarrationStore get() = store

    fun open(book: LibraryBook, accessToken: String) {
        token = accessToken
        mutableState.value = NarrationUiState(
            book = book,
            readiness = NarrationReadiness.LOADING,
        )
        viewModelScope.launch {
            val sha = book.fingerprint.sha256
            val bookText = store.bookText(sha)
            if (bookText == null) {
                mutableState.value = mutableState.value.copy(
                    readiness = NarrationReadiness.UNREADABLE,
                    error = "This book's text is no longer on this device. Import it again to read it.",
                )
                return@launch
            }

            val storedScan = store.textScan(sha)
            // The scan's offsets index a text of a particular length. If the stored text is no
            // longer that length the events point at the wrong words, which is worse than
            // having none.
            val scanApplies = storedScan?.appliesTo(bookText.length) == true

            val planLoad = store.loadPlan(
                sha,
                NarrationStore.bookTextHash(bookText, EXTRACTION_VERSION),
            )
            if (planLoad is PlanLoad.Stale) store.discardStalePlan(sha, planLoad.reason)
            val plan = (planLoad as? PlanLoad.Loaded)?.plan

            val flags = localAudio.narrationFlags(sha)
            val cached = localAudio.offlinePlayback(sha)

            mutableState.value = mutableState.value.copy(
                readiness = if (scanApplies) {
                    NarrationReadiness.READY
                } else {
                    NarrationReadiness.AWAITING_FILTERS
                },
                bookText = bookText,
                plan = plan,
                queue = store.loadQueue(sha),
                audioBytes = store.audioBytes(sha),
                // Both scopes, in the precedence order the renderer expects. Loaded with the book
                // rather than at render time: a render must not wait on a preferences read, and a
                // rule that failed to load must not silently mean "no rules" halfway through a book.
                pronunciationRules = PronunciationRules.scoped(
                    bookRules = localAudio.bookPronunciationRules(sha),
                    accountRules = localAudio.accountPronunciationRules(),
                ),
                scanEvents = if (scanApplies) storedScan!!.events else emptyList(),
                scannerVersion = storedScan?.scannerVersion,
                selectedVoice = localAudio.narrationVoice(sha),
                flags = flags,
                readerSettings = localAudio.readerSettings(),
                readerPosition = localAudio.readerPosition(sha),
                // The same four sets the player reads, from the same store, so a narrated book's
                // filter choices sync by the path an audiobook's already do.
                disabledCategoryIDs = cached.disabledCategoryIDs.map { it.lowercase() }.toSet(),
                disabledGroupIDs = cached.disabledGroupIDs.map { it.lowercase() }.toSet(),
                disabledEventKeys = cached.disabledEventKeys.toSet(),
                disabledAggregateKeys = cached.disabledAggregateKeys.toSet(),
                message = if (scanApplies) null else FILTERS_UNAVAILABLE_MESSAGE,
            )
        }
    }

    /**
     * Records that the listener chose to read without filter results.
     *
     * Unblocks rendering. Kept as a recorded flag rather than a transient decision because the
     * consequence -- audio that was never filtered -- outlives the session in which it was
     * chosen, and a listener who later wonders why a passage was spoken deserves an answer.
     */
    fun continueWithoutFilterResults() {
        val book = mutableState.value.book ?: return
        val flags = mutableState.value.flags.copy(continuedWithoutFilterResults = true)
        mutableState.value = mutableState.value.copy(flags = flags, message = null)
        viewModelScope.launch {
            localAudio.saveNarrationFlags(book.fingerprint.sha256, flags)
        }
    }

    fun updateReaderSettings(settings: ReaderSettings) {
        mutableState.value = mutableState.value.copy(readerSettings = settings)
        viewModelScope.launch { localAudio.saveReaderSettings(settings) }
    }

    fun saveReaderPosition(paragraphIndex: Int, scrollOffset: Int) {
        val book = mutableState.value.book ?: return
        val position = ReaderPosition(paragraphIndex.coerceAtLeast(0), scrollOffset.coerceAtLeast(0))
        if (mutableState.value.readerPosition == position) return
        mutableState.value = mutableState.value.copy(readerPosition = position)
        viewModelScope.launch {
            localAudio.saveReaderPosition(book.fingerprint.sha256, position)
        }
    }

    fun selectVoice(voice: SelectedVoice) {
        val book = mutableState.value.book ?: return
        mutableState.value = mutableState.value.copy(selectedVoice = voice)
        viewModelScope.launch { localAudio.saveNarrationVoice(book.fingerprint.sha256, voice) }
    }

    /** Reads the entitlement, forcing a check when a voice surface is being opened. */
    fun refreshTier(force: Boolean = false) {
        val accessToken = token ?: return
        viewModelScope.launch {
            val tiers = NarrationTierStore(
                readAccess = { api.accountAccess(accessToken) },
                loadRecorded = {
                    localAudio.narrationTier()?.let { (tier, plan, readAt) ->
                        NarrationTierState(
                            tier = runCatching { NarrationTier.valueOf(tier) }
                                .getOrDefault(NarrationTier.FREE),
                            plan = plan,
                            confirmedAtEpochMillis = readAt,
                            isConfirmed = true,
                        )
                    }
                },
                saveRecorded = { recorded ->
                    localAudio.saveNarrationTier(
                        recorded.tier.name,
                        recorded.plan,
                        recorded.confirmedAtEpochMillis ?: System.currentTimeMillis(),
                    )
                },
            )
            val resolved = tiers.currentTier(force)
            mutableState.value = mutableState.value.copy(tier = resolved)
        }
    }

    /**
     * Takes a filter choice, confirming first when it would invalidate audio already rendered.
     *
     * A narrated book's audio is built with the filters that were in force when it was rendered, so
     * changing one afterwards does not change what has already been synthesised. Writing the choice
     * alone would leave the book saying one thing and sounding like another, with nothing to tell
     * the listener which they are hearing. So where rendered audio is affected the choice is held
     * and confirmed; where it is not -- the common case, since most changes touch categories a book
     * never triggered -- it is written straight through, because a confirmation nobody needs is how
     * listeners learn to dismiss the one that matters.
     */
    fun setFilterChoices(
        disabledCategoryIDs: Set<String>,
        disabledGroupIDs: Set<String>,
        disabledEventKeys: Set<String>,
        disabledAggregateKeys: Set<String>,
    ) {
        val current = mutableState.value
        if (current.book == null) return

        val after = FilterChoices(
            disabledCategoryIDs = disabledCategoryIDs,
            disabledGroupIDs = disabledGroupIDs,
            disabledEventKeys = disabledEventKeys,
            disabledAggregateKeys = disabledAggregateKeys,
        )
        val impact = FilterChangeCoordinator.impactOf(
            chapters = current.plan?.chapters ?: emptyList(),
            states = current.queue?.states ?: emptyList(),
            events = current.scanEvents,
            before = current.filterChoices,
            after = after,
            voiceKind = current.selectedVoice?.kind ?: VoiceKind.SYSTEM,
        )

        when (impact) {
            FilterChangeImpact.None -> commitFilterChoices(after)
            is FilterChangeImpact.Rerender -> {
                // Nothing written and nothing discarded until they confirm. The dialogue reads the
                // choice back from here, so declining needs no separate record of the old one.
                mutableState.value = current.copy(
                    pendingFilterChange = PendingFilterChange(after, impact),
                )
            }
        }
    }

    /**
     * Applies a held filter change: discards the affected audio and renders it again.
     *
     * The offset is taken before anything is discarded. Re-rendering changes chapter durations, so
     * the Book_Time the listener is at stops denoting the same words the moment audio is replaced --
     * their place in the book survives as a character offset or not at all.
     */
    fun confirmFilterChange(context: android.content.Context) {
        val current = mutableState.value
        val pending = current.pendingFilterChange ?: return
        val book = current.book ?: return
        val plan = current.plan ?: return
        val affected = pending.impact.affectedChapterIndices.toSet()

        mutableState.value = current.copy(pendingFilterChange = null)
        commitFilterChoices(pending.choices)
        rerenderChapters(context, pending.impact.affectedChapterIndices)
    }

    /**
     * Discards the given chapters' audio and makes it again, keeping the listener's place.
     *
     * Shared by the two things that can invalidate audio already made -- a filter change and a
     * pronunciation change. The reason the audio is wrong differs; what has to happen to it does not,
     * and two implementations of "discard, requeue, restore" would drift apart in exactly the ways
     * that lose someone's place in a book.
     */
    private fun rerenderChapters(context: android.content.Context, chapterIndices: List<Int>) {
        val current = mutableState.value
        val book = current.book ?: return
        val plan = current.plan ?: return
        if (chapterIndices.isEmpty()) return
        val affected = chapterIndices.toSet()

        mutableState.value = current.copy(message = "Making this book's audio match your changes…")

        viewModelScope.launch {
            val sha256 = book.fingerprint.sha256

            // Taken before anything is discarded, and as a character offset rather than a time.
            // Re-rendering changes chapter durations, so the Book_Time the listener is at stops
            // denoting the same words the moment audio is replaced. The offset is the only form of
            // their place in the book that survives the operation.
            val offset = characterOffsetOfPosition(sha256, current)

            // Waited for, not merely cancelled. `cancel()` returns before the coroutine has
            // stopped, and the renderer finishes a chapter with `partial.renameTo(destination)` --
            // a plain filesystem call with no cancellation point in it. A cancelled render can
            // therefore still publish a chapter *after* the delete loop below has run, restoring
            // audio built from the settings that were just replaced. That is the exact
            // inconsistency this path exists to remove, so the old pass is joined before anything
            // is deleted.
            renderJob?.cancelAndJoin()
            positionJob?.cancel()
            playback?.pause()
            mutableState.value = mutableState.value.copy(isSpeaking = false)

            affected.forEach { index ->
                store.deleteChapterAudio(sha256, index)
                store.deleteChapterTimeline(sha256, index)
            }
            val cleared = store.loadQueue(sha256)?.let { queue ->
                queue.copy(
                    states = queue.states.mapIndexed { index, state ->
                        if (index in affected) RenderState.NOT_RENDERED else state
                    },
                    chapterDurationsMs = queue.chapterDurationsMs.mapIndexed { index, duration ->
                        if (index in affected) 0L else duration
                    },
                )
            }
            if (cleared != null) {
                store.saveQueue(sha256, cleared)
                mutableState.value = mutableState.value.copy(queue = cleared)
            }

            // The chapter they were in is rendered first, so the wait is as short as it can be for
            // the one place in the book they are actually listening to.
            val playhead = offset
                ?.let { character -> plan.chapters.indexOfFirst { character < it.endCharacter } }
                ?.takeIf { it >= 0 }
                ?: 0

            // Reuses the ordinary render-then-play path rather than re-deriving filtered ranges and
            // a coordinator here, so the new audio is built from the settings just committed.
            renderThenPlay(context, sha256, plan, chapterIndex = playhead)

            // Awaited, because `renderThenPlay` launches its work and returns. Restoring straight
            // after it would read a timeline in which the discarded chapters are still absent: the
            // offset would map to nothing, or -- worse -- map into a surviving chapter whose book
            // time has shifted now that the discarded durations are zero, and seek confidently to
            // the wrong words.
            renderJob?.join()

            restorePositionAfterRerender(sha256, plan, offset)
        }
    }

    /**
     * Where the listener is, as a character offset into the book's text.
     *
     * Built from the timeline of what is currently rendered, because that is the only thing relating
     * a Book_Time to words. Null when nothing is rendered, or when the position falls in a gap
     * between rendered chapters -- in which case the position is left alone rather than guessed at.
     */
    private suspend fun characterOffsetOfPosition(
        sha256: String,
        state: NarrationUiState,
    ): Int? {
        val timeline = renderedTimeline(sha256, state.queue) ?: return null
        return readerCharacterForTime(timeline.narrationTimingRanges, state.positionSeconds)
    }

    /**
     * Puts the listener back where they were, once audio holding that place exists again.
     *
     * Silent when the offset cannot be mapped back into the new audio. A position that cannot be
     * restored honestly is better left at the start of the re-rendered chapter than moved somewhere
     * that merely looks plausible.
     */
    private suspend fun restorePositionAfterRerender(
        sha256: String,
        plan: NarrationPlan,
        offset: Int?,
    ) {
        mutableState.value = mutableState.value.copy(message = null)
        if (offset == null) return
        val timeline = renderedTimeline(sha256, mutableState.value.queue) ?: return
        val seconds = readerTimeForCharacter(timeline.narrationTimingRanges, offset) ?: return
        val (itemIndex, inItemMs) = timeline.locate((seconds * 1_000).toLong())
        val planIndex = timeline.chapters.getOrNull(itemIndex)?.planIndex ?: return
        mutableState.value = mutableState.value.copy(positionSeconds = seconds)
        playback?.play(sha256, plan, planIndex, inItemMs / 1_000.0)
    }

    /** The timeline of whatever currently has audio, or null when nothing does. */
    private suspend fun renderedTimeline(sha256: String, queue: RenderQueue?): NarrationTimeline? {
        if (queue == null) return null
        val rendered = queue.states.indices.filter { queue.states[it] == RenderState.RENDERED }
        if (rendered.isEmpty()) return null
        val timings = rendered.associateWith { index ->
            store.loadChapterTimeline(sha256, index) ?: emptyList()
        }
        return NarrationTimeline.of(
            renderedPlanIndices = rendered,
            durationsMs = { queue.chapterDurationsMs.getOrElse(it) { 0L } },
            timings = { timings[it] ?: emptyList() },
        )
    }

    /**
     * Drops a held filter change, leaving both the choice and the audio exactly as they were.
     *
     * The UI reads its switches from state, which was never changed, so they revert by themselves.
     */
    fun declineFilterChange() {
        mutableState.value = mutableState.value.copy(pendingFilterChange = null)
    }

    /**
     * Re-reads how much space this book's audio takes.
     *
     * Measured from the files rather than accumulated as rendering proceeds. A counter would drift
     * against a directory that eviction, a discard and a re-render all write to, and the number is
     * cheap enough to simply read.
     */
    private fun refreshAudioBytes() {
        val sha256 = mutableState.value.book?.fingerprint?.sha256 ?: return
        viewModelScope.launch {
            val bytes = store.audioBytes(sha256)
            mutableState.value = mutableState.value.copy(audioBytes = bytes)
        }
    }

    /** Offers the discard, with the space it frees and the chapters it costs. Discards nothing yet. */
    fun offerDiscardAllAudio() {
        val current = mutableState.value
        mutableState.value = current.copy(
            pendingDiscardAll = NarrationStorage.discardEstimate(
                audioBytes = current.audioBytes,
                states = current.queue?.states ?: emptyList(),
            ),
        )
    }

    fun cancelDiscardAllAudio() {
        mutableState.value = mutableState.value.copy(pendingDiscardAll = null)
    }

    /**
     * Reclaims every chapter's audio for this book.
     *
     * Keeps the plan, the timelines, the scan results, the pronunciation rules and the position. Only
     * the audio goes, which is the only part that is large and the only part that can be rebuilt
     * from what remains. Timings survive deliberately: the filters have not changed, so re-rendering
     * reproduces the same timings, and keeping them is what makes reclaiming space cheap to undo.
     */
    fun discardAllAudio() {
        val current = mutableState.value
        val sha256 = current.book?.fingerprint?.sha256 ?: return
        mutableState.value = current.copy(pendingDiscardAll = null)

        viewModelScope.launch {
            // Awaited rather than merely cancelled, for the same reason the re-render awaits it: the
            // renderer publishes a finished chapter with a plain `renameTo`, so a cancelled pass can
            // still write audio after the wipe below and leave a chapter that reports itself
            // reclaimed while occupying space.
            renderJob?.cancelAndJoin()
            positionJob?.cancel()
            playback?.pause()
            playback?.release()
            playback = null

            store.deleteAllChapterAudio(sha256)
            val cleared = store.loadQueue(sha256)?.let { queue ->
                queue.copy(
                    states = queue.states.map { state ->
                        // A failure is kept as a failure. It has no audio to reclaim, and calling it
                        // not-rendered would quietly discard the record that it could not be made.
                        if (state == RenderState.RENDERED) RenderState.NOT_RENDERED else state
                    },
                    chapterDurationsMs = queue.chapterDurationsMs.map { 0L },
                )
            }
            if (cleared != null) {
                store.saveQueue(sha256, cleared)
                mutableState.value = mutableState.value.copy(queue = cleared)
            }
            mutableState.value = mutableState.value.copy(
                isSpeaking = false,
                // The position is kept on purpose. It is a Book_Time into audio that no longer
                // exists, but it is restored through the timings, which do.
                message = null,
            )
            refreshAudioBytes()
        }
    }

    // region how words are said

    /**
     * Records or replaces a pronunciation rule, refusing it plainly where it cannot be accepted.
     *
     * Validation happens before anything is written, and a refusal keeps the entered values: making
     * someone retype a long name because one field was wrong is its own small insult.
     */
    fun recordPronunciationRule(
        written: String,
        spoken: String,
        scope: RuleScope,
        editingWritten: String? = null,
    ) {
        val current = mutableState.value
        val sha256 = current.book?.fingerprint?.sha256 ?: return

        viewModelScope.launch {
            val existing = when (scope) {
                RuleScope.BOOK -> localAudio.bookPronunciationRules(sha256)
                RuleScope.ACCOUNT -> localAudio.accountPronunciationRules()
            }
            val rejection = PronunciationRules.validate(
                written = written,
                spoken = spoken,
                existingInScope = existing,
                editingWritten = editingWritten,
            )
            if (rejection != null) {
                mutableState.value = mutableState.value.copy(pronunciationRejection = rejection)
                return@launch
            }

            // Order is part of what decides precedence, so a replacement keeps the position the
            // rule already held rather than moving to the end.
            val replacedIndex = editingWritten?.let { target ->
                existing.indexOfFirst { it.writtenForm.equals(target, ignoreCase = true) }
            }?.takeIf { it >= 0 }
            val rule = PronunciationRule(
                writtenForm = written.trim(),
                replacementForm = spoken.trim(),
                order = replacedIndex ?: existing.size,
            )
            val updated = if (replacedIndex != null) {
                existing.toMutableList().also { it[replacedIndex] = rule }
            } else {
                existing + rule
            }
            persistPronunciationRules(sha256, scope, updated)

            // Offered, not performed. The rule already governs everything rendered from here on;
            // redoing what exists is a separate cost and so a separate decision.
            val impact = PronunciationRules.rerenderImpact(
                chapterTexts = renderedChapterUnitTexts(),
                rule = rule,
            )
            mutableState.value = mutableState.value.copy(
                pronunciationRejection = null,
                pronunciationAccepted = mutableState.value.pronunciationAccepted + 1,
                pendingPronunciationRerender = impact.takeIf { !it.isEmpty },
            )
        }
    }

    fun deletePronunciationRule(written: String, scope: RuleScope) {
        val sha256 = mutableState.value.book?.fingerprint?.sha256 ?: return
        viewModelScope.launch {
            val existing = when (scope) {
                RuleScope.BOOK -> localAudio.bookPronunciationRules(sha256)
                RuleScope.ACCOUNT -> localAudio.accountPronunciationRules()
            }
            val removed = existing.firstOrNull {
                it.writtenForm.equals(written, ignoreCase = true)
            } ?: return@launch
            // Reindexed, because a gap in the order would change precedence between the rules that
            // remain -- a deletion should not alter how any other word is said.
            val updated = existing
                .filterNot { it.writtenForm.equals(written, ignoreCase = true) }
                .mapIndexed { index, rule -> rule.copy(order = index) }
            persistPronunciationRules(sha256, scope, updated)

            val impact = PronunciationRules.rerenderImpact(
                chapterTexts = renderedChapterUnitTexts(),
                rule = removed,
            )
            mutableState.value = mutableState.value.copy(
                pendingPronunciationRerender = impact.takeIf { !it.isEmpty },
            )
        }
    }

    /**
     * Speaks a replacement form so the listener can hear it before keeping it.
     *
     * Uses the phone's own voice, which is the free default and the one most previews will be judged
     * against. A premium preview is deliberately not offered: the synthesis provider bills per
     * character and exposes no per-utterance path, so previewing through it would spend the
     * listener's allowance on a syllable and send a fragment of their book's text off the device for
     * no result they could not get here.
     */
    fun previewPronunciation(context: android.content.Context, spoken: String) {
        val form = spoken.trim()
        if (form.isEmpty()) return
        previewJob?.cancel()
        // Released before the next synthesis, which writes the same path. Cancelling the coroutine
        // does not stop the player, so without this a second preview would rewrite the file the
        // first is still reading from.
        previewPlayer?.release()
        previewPlayer = null
        previewJob = viewModelScope.launch {
            val connection = com.audiochoice.mobile.narration.voice.TextToSpeechHandle.connect(
                context,
                java.util.Locale.US,
            )
            val handle = (connection as? com.audiochoice.mobile.narration.voice
                .TextToSpeechConnection.Connected)?.handle ?: run {
                mutableState.value = mutableState.value.copy(
                    error = "This phone has no English voice installed, so AudioChoice cannot " +
                        "play a preview.",
                )
                return@launch
            }
            handle.use { speech ->
                mutableState.value.selectedVoice?.voiceID
                    ?.takeIf { it != "system-default" }
                    ?.let(speech::selectVoice)
                // A scratch file rather than the book's audio directory: a preview is not part of
                // the book and must not be mistaken for a rendered chapter or counted as its storage.
                val file = java.io.File(context.cacheDir, "pronunciation-preview.aac")
                // A replacement form is capped at 100 characters, so this cannot run long. The cap
                // is asserted anyway, because the promise is about what the listener waits through
                // rather than about what the form happens to contain.
                val spokenForPreview = form.take(PronunciationRule.MAXIMUM_FORM_LENGTH)
                if (speech.synthesize(spokenForPreview, file) && file.length() > 0) {
                    playPreview(file)
                }
            }
        }
    }

    private fun playPreview(file: java.io.File) {
        runCatching {
            previewPlayer?.release()
            previewPlayer = android.media.MediaPlayer().apply {
                setDataSource(file.absolutePath)
                setOnCompletionListener { player ->
                    player.release()
                    if (previewPlayer === player) previewPlayer = null
                }
                prepare()
                start()
            }
        }
    }

    fun clearPronunciationRejection() {
        mutableState.value = mutableState.value.copy(pronunciationRejection = null)
    }

    fun cancelPronunciationRerender() {
        mutableState.value = mutableState.value.copy(pendingPronunciationRerender = null)
    }

    /** Re-renders the chapters a pronunciation change affects, keeping the listener's place. */
    fun confirmPronunciationRerender(context: android.content.Context) {
        val current = mutableState.value
        val impact = current.pendingPronunciationRerender ?: return
        mutableState.value = current.copy(pendingPronunciationRerender = null)
        // The same discard-and-requeue a filter change uses: the reason the audio is wrong differs,
        // what has to happen to it does not.
        rerenderChapters(context, impact.affectedChapterIndices)
    }

    private suspend fun persistPronunciationRules(
        sha256: String,
        scope: RuleScope,
        rules: List<PronunciationRule>,
    ) {
        when (scope) {
            RuleScope.BOOK -> localAudio.saveBookPronunciationRules(sha256, rules)
            RuleScope.ACCOUNT -> localAudio.saveAccountPronunciationRules(rules)
        }
        // Reloaded from both scopes rather than patched in place, so the precedence order in state
        // is always the one the renderer will actually use.
        mutableState.value = mutableState.value.copy(
            pronunciationRules = PronunciationRules.scoped(
                bookRules = localAudio.bookPronunciationRules(sha256),
                accountRules = localAudio.accountPronunciationRules(),
            ),
        )
    }

    /**
     * The spoken text of every chapter that currently has audio, by chapter index.
     *
     * Only rendered chapters, because the question a rule change raises is what to do about audio
     * that already exists. An unrendered chapter will simply be made with the new rule.
     */
    private fun renderedChapterUnitTexts(): Map<Int, List<String>> {
        val state = mutableState.value
        val plan = state.plan ?: return emptyMap()
        val states = state.queue?.states ?: return emptyMap()
        return plan.chapters.indices
            .filter { states.getOrNull(it) == RenderState.RENDERED }
            .associateWith { index ->
                plan.chapters[index].units.map { it.sourceCharacters }
            }
    }

    // endregion

    private fun commitFilterChoices(choices: FilterChoices) {
        val book = mutableState.value.book ?: return
        val disabledCategoryIDs = choices.disabledCategoryIDs
        val disabledGroupIDs = choices.disabledGroupIDs
        val disabledEventKeys = choices.disabledEventKeys
        val disabledAggregateKeys = choices.disabledAggregateKeys
        mutableState.value = mutableState.value.copy(
            disabledCategoryIDs = disabledCategoryIDs,
            disabledGroupIDs = disabledGroupIDs,
            disabledEventKeys = disabledEventKeys,
            disabledAggregateKeys = disabledAggregateKeys,
        )
        viewModelScope.launch {
            val sha = book.fingerprint.sha256
            val existing = localAudio.offlinePlayback(sha)
            localAudio.saveOfflinePlayback(
                sha,
                existing.copy(
                    disabledCategoryIDs = disabledCategoryIDs.toList(),
                    disabledGroupIDs = disabledGroupIDs.toList(),
                    disabledEventKeys = disabledEventKeys.toList(),
                    disabledAggregateKeys = disabledAggregateKeys.toList(),
                ),
            )
        }
    }

    // region reading aloud

    private var playback: NarrationPlayback? = null
    private var positionJob: kotlinx.coroutines.Job? = null
    private var renderJob: kotlinx.coroutines.Job? = null
    private var previewJob: kotlinx.coroutines.Job? = null
    private var previewPlayer: android.media.MediaPlayer? = null

    /**
     * Starts reading aloud, rendering the chapter first if it has no audio yet.
     *
     * The order matters: a chapter is rendered, then played. Nothing is spoken from text that has
     * not been through filtering and pronunciation, because by the time a voice sees a unit those
     * have already been applied to it.
     */
    fun toggleReadAloud(context: android.content.Context) {
        val current = mutableState.value
        val book = current.book ?: return
        if (!current.mayRender) return

        if (playback?.isPlaying == true) {
            playback?.pause()
            return
        }

        val plan = current.plan
        if (plan == null) {
            buildPlanThenRead(context, book.fingerprint.sha256)
            return
        }
        renderThenPlay(context, book.fingerprint.sha256, plan, chapterIndex = currentChapterIndex())
    }

    private fun currentChapterIndex(): Int {
        val queue = mutableState.value.queue ?: return 0
        // Resume where there is audio, rather than at chapter zero, so pausing and returning does
        // not restart the book.
        return NarrationPlayback.nextPlayableChapter(queue.states, queue.chapterDurationsMs, 0) ?: 0
    }

    /**
     * Builds the reading plan, then starts.
     *
     * Deferred until the listener actually asks to be read to. Segmenting a novel is a second or
     * two of work, and doing it during import would spend it on every book including the ones
     * nobody asks to hear.
     */
    private fun buildPlanThenRead(context: android.content.Context, sha256: String) {
        val bookText = mutableState.value.bookText ?: return
        mutableState.value = mutableState.value.copy(message = "Preparing this book…")
        viewModelScope.launch {
            val document = readDocument(sha256)
            if (document == null) {
                mutableState.value = mutableState.value.copy(
                    message = null,
                    error = "AudioChoice can no longer open the file this book came from, so it " +
                        "cannot work out where the chapters are. Import the EPUB again.",
                )
                return@launch
            }

            // The stored text is what every offset in the scan and the reader is measured against.
            // If a re-read produces different text, planning against it would place chapters at
            // offsets that do not match the words on screen. Refusing is the honest outcome:
            // extraction is deterministic and versioned, so this should not happen, and if it does
            // the assumption underneath the whole feature has moved.
            if (document.text.length != bookText.length) {
                mutableState.value = mutableState.value.copy(
                    message = null,
                    error = "This book's file has changed since it was imported. Import it again " +
                        "to read it aloud.",
                )
                return@launch
            }

            val plan = kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.Default) {
                StructureParser.buildPlan(
                    document = document,
                    sourceSha256 = sha256,
                    // The plan is deliberately independent of the voice: a voice with a tighter
                    // ceiling splits what it sends rather than moving a single character offset,
                    // so the ceiling here is the plan's own rather than any engine's.
                    synthesisInputLimit = SynthesisInputLimit.CEILING,
                )
            }
            if (plan == null) {
                mutableState.value = mutableState.value.copy(
                    message = null,
                    error = "This book could not be divided into chapters to read aloud.",
                )
                return@launch
            }
            store.savePlan(sha256, plan)
            mutableState.value = mutableState.value.copy(plan = plan, message = null)
            renderThenPlay(context, sha256, plan, chapterIndex = 0)
        }
    }

    private fun renderThenPlay(
        context: android.content.Context,
        sha256: String,
        plan: NarrationPlan,
        chapterIndex: Int,
    ) {
        val existing = playback ?: newPlayback(sha256, plan).also { playback = it }
        val queue = mutableState.value.queue
        val offset = NarrationPlayback.chapterOffsetSeconds(
            queue?.chapterDurationsMs.orEmpty(),
            queue?.states.orEmpty(),
            chapterIndex,
        )
        if (existing.play(sha256, plan, chapterIndex, offset)) {
            startPositionUpdates()
            renderAhead(context, sha256, plan, chapterIndex)
            return
        }
        // Falls through to render. Only reached when this chapter has no audio yet, which is the
        // ordinary case on a first press rather than a fault.

        // No audio for this chapter yet. Render it, then start.
        mutableState.value = mutableState.value.copy(
            message = "Making the audio for this chapter…",
        )
        renderAhead(context, sha256, plan, chapterIndex, thenPlay = true)
    }

    private fun renderAhead(
        context: android.content.Context,
        sha256: String,
        plan: NarrationPlan,
        chapterIndex: Int,
        thenPlay: Boolean = false,
    ) {
        // Never cancel a render in progress. Pressing the button again used to kill the pass that
        // was already working and start another, so an impatient second press made the wait
        // longer rather than shorter -- and repeated presses could keep a book from ever
        // finishing a chapter.
        if (renderJob?.isActive == true) return
        renderJob = viewModelScope.launch {
            var renderFailure: String? = null
            val voice = mutableState.value.selectedVoice
            val connection = com.audiochoice.mobile.narration.voice.TextToSpeechHandle.connect(
                context,
                java.util.Locale.US,
            )
            val handle = when (connection) {
                is com.audiochoice.mobile.narration.voice.TextToSpeechConnection.Connected ->
                    connection.handle

                com.audiochoice.mobile.narration.voice.TextToSpeechConnection.NoEngineInstalled -> {
                    mutableState.value = mutableState.value.copy(
                        message = null,
                        error = "This phone has no voice installed. Install Google " +
                            "Text-to-Speech, then try again.",
                    )
                    return@launch
                }

                is com.audiochoice.mobile.narration.voice.TextToSpeechConnection.LanguageUnavailable -> {
                    mutableState.value = mutableState.value.copy(
                        message = null,
                        error = "This phone has no English voice installed. Add one in Settings, " +
                            "then try again.",
                    )
                    return@launch
                }
            }

            handle.use { speech ->
                voice?.voiceID?.takeIf { it != "system-default" }?.let(speech::selectVoice)
                // The premium voice is used only when the gate allows it. A listener whose
                // subscription lapsed, or who has not accepted the current agreement, silently
                // falls back to the on-device voice rather than being refused: their book keeps
                // being read, and the reason is reported separately on the voice surface.
                val usePremium = voice?.kind == com.audiochoice.mobile.data.VoiceKind.PREMIUM &&
                    com.audiochoice.mobile.narration.voice.PremiumVoiceAgreement.maySubmit(
                        mutableState.value.premiumGate,
                    )
                val engine = if (usePremium) {
                    premiumEngine(sha256, voice!!.voiceID)
                } else {
                    com.audiochoice.mobile.narration.voice.SystemVoiceEngine(
                        speech = speech,
                        voiceID = speech.defaultVoiceName() ?: "system-default",
                        writerFactory = { file ->
                            com.audiochoice.mobile.narration.voice.AacChapterAudioWriter(file)
                        },
                        scratchDirectory = java.io.File(store.bookDirectory(sha256), "scratch"),
                    )
                }
                val coordinator = NarrationRenderCoordinator(
                    store = store,
                    engine = engine,
                    // Supplied from the rules loaded with the book. Left at its default this is the
                    // identity function, which is what made every stored rule inert.
                    pronounce = { text ->
                        PronunciationRules.apply(text, mutableState.value.pronunciationRules)
                    },
                    // Read from the volume the audio is actually written to, not from the device's
                    // headline figure: on a device with several volumes those differ, and the one
                    // that matters is the one about to be written.
                    freeBytes = {
                        runCatching { store.bookDirectory(sha256).usableSpace }
                            .getOrNull()
                            ?.takeIf { it > 0 }
                    },
                    onProgress = { progress ->
                        mutableState.value = mutableState.value.copy(
                            message = progress.renderingChapterTitle
                                ?.let { "Making the audio for $it…" },
                        )
                    },
                )
                val filtered = FilteredRanges.forEnabledEvents(
                    events = mutableState.value.scanEvents,
                    disabledCategoryIDs = mutableState.value.disabledCategoryIDs,
                    disabledGroupIDs = mutableState.value.disabledGroupIDs,
                    disabledEventKeys = mutableState.value.disabledEventKeys,
                    disabledAggregateKeys = mutableState.value.disabledAggregateKeys,
                )
                // The exception is kept rather than discarded: swallowing it with getOrNull()
                // was what turned a real fault into a button that appeared to do nothing.
                val attempt = runCatching {
                    coordinator.renderPending(
                        sha256 = sha256,
                        plan = plan,
                        filteredRanges = filtered,
                        playheadChapter = chapterIndex,
                    )
                }
                val pass = attempt.getOrNull()
                renderFailure = attempt.exceptionOrNull()
                    ?.let { it.message ?: it::class.java.simpleName }

                mutableState.value = mutableState.value.copy(
                    queue = pass?.queue ?: mutableState.value.queue,
                    message = null,
                    // Told plainly, and told as a thing they can act on. The chapters already made
                    // are kept, so freeing space and asking again continues rather than restarts.
                    error = if (pass?.stopReason == StopReason.OUT_OF_STORAGE) {
                        "There is not enough free space to make more audio for this book. " +
                            "The chapters already made are kept. Free up some space and tap " +
                            "Read aloud again."
                    } else {
                        mutableState.value.error
                    },
                )
                // Rendering is the one thing that grows this figure, so it is re-read here rather
                // than on a timer.
                refreshAudioBytes()

                if (thenPlay) {
                    val readyQueue = pass?.queue
                    // Searched from the start, not from the requested chapter: the front of a
                    // book is routinely a title page and a copyright notice, which render as
                    // silence, so a book whose first speakable chapter is the third would
                    // otherwise report that it could not be read at all.
                    val startAt = readyQueue?.let {
                        NarrationPlayback.nextPlayableChapter(it.states, it.chapterDurationsMs, 0)
                    }
                    if (startAt == null) {
                        // Reports the actual reason rather than a guess. The coordinator records
                        // one per chapter, and a message naming the real fault is the difference
                        // between a fixable report and "it does nothing".
                        val reason = readyQueue?.failureReasons?.values?.firstOrNull()
                        val silentOnly = readyQueue != null &&
                            readyQueue.states.any { it == RenderState.RENDERED } &&
                            !NarrationPlayback.hasAnyAudio(
                                readyQueue.states, readyQueue.chapterDurationsMs,
                            )
                        mutableState.value = mutableState.value.copy(
                            error = when {
                                reason != null -> "That chapter could not be read aloud: $reason"
                                silentOnly ->
                                    "Every part of this book that was prepared turned out to have " +
                                        "no readable text, so there is nothing to read aloud yet."
                                readyQueue == null ->
                                    "Preparing the audio failed before it started. " +
                                        (renderFailure ?: "No further detail was reported.")
                                else -> "No audio was produced for this book."
                            },
                        )
                    } else {
                        val player = playback ?: newPlayback(sha256, plan).also { playback = it }
                        val offset = NarrationPlayback.chapterOffsetSeconds(
                            readyQueue.chapterDurationsMs, readyQueue.states, startAt,
                        )
                        if (player.play(sha256, plan, startAt, offset)) {
                            startPositionUpdates()
                        } else {
                            // Never silent. A play() that returns false means the file is missing
                            // or unreadable, and saying so is the difference between a bug report
                            // and "the button does nothing".
                            mutableState.value = mutableState.value.copy(
                                error = "Chapter ${startAt + 1} was prepared but its audio could " +
                                    "not be opened. Try Read aloud again.",
                            )
                        }
                    }
                }
            }
        }
    }

    /**
     * The premium engine for this book.
     *
     * Built per render rather than held, because it closes over the book's fingerprint and the
     * access token, and both change with the book and the session.
     */
    private fun premiumEngine(
        sha256: String,
        voiceID: String,
    ): com.audiochoice.mobile.narration.voice.PremiumVoiceEngine {
        val accessToken = token
        val fingerprint = mutableState.value.book!!.fingerprint
        return com.audiochoice.mobile.narration.voice.PremiumVoiceEngine(
            voiceID = voiceID,
            fingerprint = fingerprint,
            submit = { request ->
                requireNotNull(accessToken) { "No session for premium synthesis" }
                api.submitNarrationChapter(accessToken, request).jobID
            },
            poll = { jobID ->
                api.narrationChapter(requireNotNull(accessToken), jobID)
            },
            saveTimeline = { chapterIndex, timings ->
                store.saveChapterTimeline(sha256, chapterIndex, timings)
            },
        )
    }

    /**
     * Reads the voices the server offers, and the agreement premium requires.
     *
     * Also delivers an acceptance recorded while offline. Delivery is idempotent on the version at
     * the far end, so re-sending on every read costs nothing and cannot create a second record.
     */
    fun refreshVoices() {
        val accessToken = token ?: return
        viewModelScope.launch {
            val recorded = localAudio.premiumVoiceAcknowledgement()?.let { stored ->
                runCatching {
                    kotlinx.serialization.json.Json { ignoreUnknownKeys = true }
                        .decodeFromString<com.audiochoice.mobile.narration.voice.PremiumAgreementRecord>(
                            stored,
                        )
                }.getOrNull()
            }
            mutableState.value = mutableState.value.copy(agreementRecord = recorded)

            val voices = runCatching { api.narrationVoices(accessToken) }.getOrNull() ?: return@launch
            mutableState.value = mutableState.value.copy(
                premiumVoices = voices.voices,
                agreementVersion = voices.agreementVersion,
                agreementText = voices.agreementText,
            )

            if (com.audiochoice.mobile.narration.voice.PremiumVoiceAgreement.needsDelivery(recorded)) {
                deliverAgreement(accessToken, recorded!!)
            }
        }
    }

    /** Records the listener's acceptance, locally first so it survives no signal. */
    fun acceptPremiumAgreement() {
        val version = mutableState.value.agreementVersion ?: return
        val text = mutableState.value.agreementText.orEmpty()
        val record = com.audiochoice.mobile.narration.voice.PremiumVoiceAgreement.accept(
            version, text, System.currentTimeMillis(),
        )
        // Local first, and sufficient on its own: an acceptance that needed the network would be
        // unrecordable exactly when a listener is least able to do anything about it.
        mutableState.value = mutableState.value.copy(agreementRecord = record)
        viewModelScope.launch {
            persistAgreement(record)
            token?.let { deliverAgreement(it, record) }
        }
    }

    private suspend fun deliverAgreement(
        accessToken: String,
        record: com.audiochoice.mobile.narration.voice.PremiumAgreementRecord,
    ) {
        val delivered = runCatching {
            api.acceptNarrationAgreement(
                accessToken,
                com.audiochoice.contracts.NarrationAcknowledgementRequest(
                    agreementVersion = record.version,
                    agreementText = record.text,
                ),
            )
        }.isSuccess
        if (!delivered) return
        // Marked delivered only on confirmation. Until then it is re-sent on the next read, so an
        // acceptance made offline is never lost.
        val confirmed = record.copy(deliveredToBackend = true)
        persistAgreement(confirmed)
        mutableState.value = mutableState.value.copy(agreementRecord = confirmed)
    }

    private suspend fun persistAgreement(
        record: com.audiochoice.mobile.narration.voice.PremiumAgreementRecord,
    ) {
        localAudio.savePremiumVoiceAcknowledgement(
            kotlinx.serialization.json.Json { ignoreUnknownKeys = true }
                .encodeToString(record),
        )
    }

    private fun newPlayback(sha256: String, plan: NarrationPlan) = NarrationPlayback(
        store = store,
        onPositionSeconds = { seconds ->
            mutableState.value = mutableState.value.copy(positionSeconds = seconds)
        },
        onChapterFinished = { finished ->
            // Straight into the next chapter that has audio. A chapter that failed to render is
            // stepped over rather than ending the book.
            val queue = mutableState.value.queue ?: return@NarrationPlayback
            val next = NarrationPlayback.nextPlayableChapter(
                queue.states, queue.chapterDurationsMs, finished + 1,
            )
            if (next == null) {
                mutableState.value = mutableState.value.copy(isSpeaking = false)
            } else {
                val offset = NarrationPlayback.chapterOffsetSeconds(
                    queue.chapterDurationsMs, queue.states, next,
                )
                playback?.play(sha256, plan, next, offset)
            }
        },
        onStateChanged = { speaking ->
            mutableState.value = mutableState.value.copy(isSpeaking = speaking)
        },
    )

    /**
     * Publishes the position while speaking, so the reader can follow along.
     *
     * Four times a second: fast enough that the highlight lands on the paragraph being read,
     * slow enough that it is not recomposing the reader on every frame.
     */
    private fun startPositionUpdates() {
        positionJob?.cancel()
        positionJob = viewModelScope.launch {
            while (playback?.isPlaying == true) {
                mutableState.value = mutableState.value.copy(
                    positionSeconds = playback?.positionSeconds ?: 0.0,
                )
                kotlinx.coroutines.delay(250)
            }
        }
    }

    // endregion

    fun dismissMessage() {
        mutableState.value = mutableState.value.copy(message = null, error = null)
    }

    fun close() {
        positionJob?.cancel()
        renderJob?.cancel()
        previewJob?.cancel()
        previewPlayer?.release()
        previewPlayer = null
        playback?.release()
        playback = null
        mutableState.value = NarrationUiState()
    }

    override fun onCleared() {
        // The player holds a native decoder and a file handle. Leaking it survives the screen and
        // keeps reading aloud into a library the listener has already navigated away from.
        positionJob?.cancel()
        renderJob?.cancel()
        previewJob?.cancel()
        previewPlayer?.release()
        previewPlayer = null
        playback?.release()
        playback = null
        super.onCleared()
    }

    companion object {
        /**
         * Matches the extraction version the plan was built against.
         *
         * Kept beside the loader because a mismatch is what makes a stale plan detectable, and a
         * wrong value here would either discard every plan or trust one built against text that
         * has moved.
         */
        const val EXTRACTION_VERSION = 1

        const val FILTERS_UNAVAILABLE_MESSAGE =
            "Filter results for this book aren't ready yet, so nothing is being filtered. " +
                "You can read it now, but it won't be read aloud until filters load or you " +
                "choose to continue without them."
    }

    class Factory(
        private val context: android.content.Context,
        private val api: AudioChoiceApi,
        private val localAudio: LocalAudioStore,
        private val filesDirectory: File,
    ) : ViewModelProvider.Factory {
        @Suppress("UNCHECKED_CAST")
        override fun <T : ViewModel> create(modelClass: Class<T>): T = NarrationViewModel(
            api = api,
            localAudio = localAudio,
            filesDirectory = filesDirectory,
            readDocument = { sha256 ->
                // The URI recorded at import, which the persistable permission keeps readable.
                val uri = localAudio.epub(sha256)
                if (uri == null) {
                    null
                } else {
                    runCatching {
                        com.audiochoice.mobile.reader.EpubTextReader.readNarrationDocument(
                            context.contentResolver, uri,
                        )
                    }.getOrNull()?.takeIf { it.text.isNotEmpty() && it.resources.isNotEmpty() }
                }
            },
        ) as T
    }
}

/** The voices to offer, given the tier and what the device supports. */
fun NarrationUiState.availableVoiceKinds(localNeuralSupported: Boolean): List<VoiceKind> =
    NarrationTiers.availableVoiceKinds(
        tier?.tier ?: NarrationTier.FREE,
        localNeuralSupported,
    )
