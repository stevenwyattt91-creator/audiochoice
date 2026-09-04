package com.audiochoice.mobile.player

import android.annotation.SuppressLint
import android.content.ComponentName
import android.content.Context
import android.net.Uri
import android.content.SharedPreferences
import androidx.core.content.ContextCompat
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.session.MediaController
import androidx.media3.session.SessionToken
import com.audiochoice.mobile.BuildConfig
import com.audiochoice.mobile.beta.BetaConfig
import kotlin.math.roundToInt
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import com.audiochoice.mobile.importing.EditionSignatures
import com.audiochoice.mobile.importing.Mp4TagReader
import kotlinx.serialization.json.Json
import com.audiochoice.contracts.EditionSignature
import com.audiochoice.mobile.data.ApiException
import com.audiochoice.mobile.data.AudioChoiceApi
import com.audiochoice.mobile.data.AudioChapter
import com.audiochoice.mobile.data.FilterReportComposer
import com.audiochoice.mobile.data.FilterReportQueue
import com.audiochoice.mobile.data.FilterReportRequest
import com.audiochoice.mobile.data.EditionSignatureReportRequest
import com.audiochoice.mobile.data.LibraryBook
import com.audiochoice.mobile.data.LibraryBookmark
import com.audiochoice.mobile.data.BookFilterSettingsUpsertRequest
import com.audiochoice.mobile.data.LocalAudioStore
import com.audiochoice.mobile.data.OfflineBookPlayback
import com.audiochoice.mobile.data.PendingBookmark
import com.audiochoice.mobile.data.ReaderTimingRange
import com.audiochoice.mobile.playback.AudioChoicePlaybackService
import com.audiochoice.mobile.reader.EpubTextReader
import com.audiochoice.mobile.reader.ReaderParagraph
import com.audiochoice.mobile.reader.ReaderParagraphParser
import com.audiochoice.mobile.reader.ReaderPosition
import com.audiochoice.mobile.reader.ReaderSettings
import com.audiochoice.contracts.ScanEvent
import kotlinx.coroutines.delay
import kotlinx.coroutines.async
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.filterNotNull
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.util.UUID

/**
 * Whether the filter profile driving playback can be trusted right now.
 *
 * An empty event list is ambiguous on its own: it means either "this audiobook
 * is genuinely clean" or "the scan could not be loaded". Conflating the two let
 * the player report "Clean" while skipping nothing, so a listener could hear
 * unfiltered content believing their filters were active.
 */
enum class FilterAvailability {
    /** The scan lookup has not finished yet. */
    LOADING,

    /** Fetched from the server during this session. */
    LIVE,

    /** Server unreachable, but a previously saved scan for this book was used. */
    CACHED,

    /** No scan available from the server or on this device. Nothing is filtered. */
    UNAVAILABLE,
}

data class PlayerUiState(
    val book: LibraryBook? = null,
    val localUri: Uri? = null,
    val isPlaying: Boolean = false,
    val positionMs: Long = 0,
    val durationMs: Long = 0,
    val speed: Float = 1f,
    val sleepSecondsRemaining: Int? = null,
    val bookmarks: List<LibraryBookmark> = emptyList(),
    val chapters: List<AudioChapter> = emptyList(),
    val coverPath: String? = null,
    val scanEvents: List<ScanEvent> = emptyList(),
    val scannerVersion: String? = null,
    val filterAvailability: FilterAvailability = FilterAvailability.LOADING,
    val disabledCategoryIDs: Set<String> = emptySet(),
    val disabledGroupIDs: Set<String> = emptySet(),
    val disabledEventKeys: Set<String> = emptySet(),
    val disabledAggregateKeys: Set<String> = emptySet(),
    val bookmarkSaved: Boolean = false,
    /** Set after a filter report is queued, so the player can confirm it. */
    val filterReportSent: Boolean = false,
    /**
     * Present only for a narrated book, and the marker the two playback guards test on.
     *
     * Null for every imported audiobook, which is what keeps both guards inert on the
     * path that ships today.
     */
    val narration: NarrationPlaybackState? = null,
    val error: String? = null,
    val epubText: String? = null,
    /** Parsed once per book rather than on every recomposition. */
    val readerParagraphs: List<ReaderParagraph> = emptyList(),
    val readerTimingRanges: List<ReaderTimingRange> = emptyList(),
    val readerSyncMessage: String? = null,
    val readerSettings: ReaderSettings = ReaderSettings(),
    /** Where to restore the reader to for the current book. */
    val readerPosition: ReaderPosition = ReaderPosition(),
    val isReady: Boolean = false,
)

class PlayerViewModel(
    // Factory always supplies applicationContext, so this outlives the Activity
    // by design and cannot leak it.
    @SuppressLint("StaticFieldLeak")
    private val context: Context,
    private val api: AudioChoiceApi,
    private val localAudio: LocalAudioStore,
) : ViewModel() {
    // The ExoPlayer itself lives in AudioChoicePlaybackService so playback can
    // survive backgrounding. This ViewModel drives it through a MediaController,
    // which implements the same Player interface. The connection is asynchronous,
    // so every transport call is null-safe and reads fall back to the last known
    // values captured by the polling loop.
    private var controller: MediaController? = null
    private val connectedController = MutableStateFlow<MediaController?>(null)
    private var controllerFuture: com.google.common.util.concurrent.ListenableFuture<MediaController>? = null
    /**
     * The position playback must reach before it may begin, and which a checkpoint
     * must not overwrite until reached. Null once satisfied or when resuming from
     * the very start, where there is nothing to protect.
     */
    private var pendingResumeMs: Long? = null
    private var lastKnownPositionMs = 0L
    private var lastKnownDurationMs = 0L
    private val mutableState = MutableStateFlow(PlayerUiState())
    val state: StateFlow<PlayerUiState> = mutableState.asStateFlow()
    private var token: String? = null
    private var lastSavedSecond = -1L
    private var sleepJob: kotlinx.coroutines.Job? = null
    private var sleepAtPositionMs: Long? = null
    private var pendingFilterSeekTargetMs: Long? = null
    private var hasStartedPlayback = false
    private var openJob: kotlinx.coroutines.Job? = null
    private val progressSaveMutexes = mutableMapOf<String, Mutex>()
    private val latestProgressMs = mutableMapOf<String, Long>()
    private val progressSaveCallbacks = mutableMapOf<String, MutableList<() -> Unit>>()
    private val savedPositions = mutableMapOf<String, Double>()
    // Keep a device-local checkpoint so switching books or closing the app never
    // loses the last position when the network request is delayed or unavailable.
    private val localProgress: SharedPreferences = context.getSharedPreferences(
        PlaybackProgressKeys.PREFERENCES_NAME,
        Context.MODE_PRIVATE,
    )
    // Reports are written here before being sent, so one made without signal survives.
    private val filterReports = FilterReportQueue(
        context,
        Json { ignoreUnknownKeys = true; encodeDefaults = true },
        api,
    )

    /**
     * Returns the ID of the last book that was playing before the process was
     * killed, or null if nothing was open. Used by the UI to auto-restore the
     * player screen after an Activity recreation.
     */
    fun lastOpenBookID(): String? = localProgress.getString(LAST_BOOK_ID_KEY, null)

    /**
     * Work that must happen once per process. Lives here so it inherits this object's
     * lifetime: kept across a configuration change, gone when the process is recreated.
     */
    private val oncePerProcess = ProcessOnceClaims()

    /** True the first time this process reconciles [userID]'s stored progress. */
    fun beginAccountProgressHydration(userID: String): Boolean =
        oncePerProcess.claim("hydrate", userID)

    /**
     * True the first time this process reopens [userID]'s last book.
     *
     * Claimed rather than inferred from `book == null` so a listener who closed their book
     * does not have it reopened underneath them on the next recomposition.
     */
    fun beginLastOpenBookRestore(userID: String): Boolean =
        oncePerProcess.claim("restore", userID)

    /**
     * Current playback position. Falls back to the last value the polling loop
     * observed so a momentarily disconnected controller cannot report 0 and
     * cause a progress checkpoint to overwrite a real position with the start
     * of the book.
     */
    /**
     * A transport whose reported position can be believed, or null.
     *
     * This distinction is the whole ballgame for progress. A MediaController
     * whose service has gone away is **still a non-null object**, and it reports
     * position 0. Reading it directly therefore turned "the player was released"
     * into "the listener is at the start of the book", and the checkpoint written
     * on the way out recorded 0 over a perfectly good position.
     *
     * That is not hypothetical: pausing and swiping the app away makes the
     * service see `!playWhenReady`, call stopSelf() and release the player, all
     * potentially before Activity.onStop() runs its save.
     */
    private val liveTransport: MediaController?
        get() = controller?.takeIf { it.isConnected && it.playbackState != Player.STATE_IDLE }

    /**
     * Converts the controller's numbers into book position and duration.
     *
     * [DirectPlaybackTimeline] reports the controller's own values unchanged, so an
     * imported audiobook behaves exactly as it did before this indirection existed. A
     * narrated book swaps in a timeline that accumulates across rendered chapters,
     * because its playlist holds one item per chapter rather than one item per book.
     *
     * Routed through one property rather than converted at each of the fourteen reads
     * below, since missing one of those -- most easily the progress checkpoint -- writes a
     * wrong resume position to the account with nothing to show that it happened.
     */
    private var playbackTimeline: PlaybackTimeline = DirectPlaybackTimeline

    /** Null when nothing trustworthy can report a position right now. */
    private val trustedPositionMs: Long?
        get() = liveTransport
            ?.let { playbackTimeline.bookPositionMs(it.currentMediaItemIndex, it.currentPosition) }
            ?.coerceAtLeast(0L)

    private val currentPositionMs: Long
        get() = trustedPositionMs ?: lastKnownPositionMs

    /** Raw duration, which is [androidx.media3.common.C.TIME_UNSET] until known. */
    private val rawDurationMs: Long
        get() = liveTransport?.let { playbackTimeline.bookDurationMs(it.duration) }
            ?: lastKnownDurationMs

    private val isPlayingNow: Boolean
        get() = controller?.isPlaying == true

    private val playerListener = object : Player.Listener {
        override fun onIsPlayingChanged(isPlaying: Boolean) {
            mutableState.value = mutableState.value.copy(isPlaying = isPlaying)
            if (isPlaying) {
                hasStartedPlayback = true
                enforceEnabledFilters(currentPositionMs, allowLookAhead = true)
            } else if (hasStartedPlayback) {
                // Pausing, an interruption, or the app moving to the
                // background must retain the exact last listening point.
                saveProgress()
            }
        }

        override fun onPlaybackStateChanged(playbackState: Int) {
            mutableState.value = mutableState.value.copy(
                isReady = playbackState == Player.STATE_READY,
                durationMs = rawDurationMs.coerceAtLeast(0),
            )
        }

        override fun onPlayerError(error: PlaybackException) {
            mutableState.value = mutableState.value.copy(
                isPlaying = false,
                isReady = false,
                error = "This audiobook could not be played (${error.errorCodeName}). Re-import it so AudioChoice can save a stable local copy.",
            )
        }

        override fun onPositionDiscontinuity(
            oldPosition: Player.PositionInfo,
            newPosition: Player.PositionInfo,
            reason: Int,
        ) {
            // Every seek is a fresh filter evaluation. This includes slider
            // scrubs, chapter/bookmark jumps, 30-second skips and rewinds.
            pendingFilterSeekTargetMs = null
            enforceEnabledFilters(newPosition.positionMs, allowLookAhead = isPlayingNow)
        }
    }

    init {
        val sessionToken = SessionToken(
            context,
            ComponentName(context, AudioChoicePlaybackService::class.java),
        )
        val pending = MediaController.Builder(context, sessionToken).buildAsync()
        controllerFuture = pending
        pending.addListener({
            val connected = runCatching { pending.get() }.getOrNull()
            if (connected == null) {
                mutableState.value = mutableState.value.copy(
                    error = "AudioChoice could not start playback on this device.",
                )
                return@addListener
            }
            controller = connected
            connected.addListener(playerListener)
            // Adopt whatever the service is already playing. After an Activity
            // recreation the book can still be running in the background.
            mutableState.value = mutableState.value.copy(
                isPlaying = connected.isPlaying,
                isReady = connected.playbackState == Player.STATE_READY,
            )
            connectedController.value = connected
        }, ContextCompat.getMainExecutor(context))

        viewModelScope.launch {
            // Nothing to poll until the service connection exists.
            connectedController.filterNotNull().first()
            while (isActive) {
                // Only ever cache a believable reading. The old unguarded reads
                // poisoned this cache with the 0 that a released controller
                // reports, which is what the save path then persisted.
                trustedPositionMs?.let { lastKnownPositionMs = it }
                liveTransport?.duration?.takeIf { it > 0 }?.let { lastKnownDurationMs = it }
                if (mutableState.value.book != null) {
                    val position = currentPositionMs
                    mutableState.value = mutableState.value.copy(
                        positionMs = position,
                        durationMs = rawDurationMs.coerceAtLeast(0),
                    )
                    val second = position / 1000
                    enforceEnabledFilters(position, allowLookAhead = isPlayingNow)
                    sleepAtPositionMs?.let { target ->
                        if (isPlayingNow && position >= target) {
                            controller?.pause()
                            sleepAtPositionMs = null
                            mutableState.value = mutableState.value.copy(sleepSecondsRemaining = null)
                            saveProgress()
                        } else {
                            mutableState.value = mutableState.value.copy(
                                sleepSecondsRemaining = ((target - position).coerceAtLeast(0) / 1000).toInt(),
                            )
                        }
                    }
                    markFinishedIfAtEnd(position, rawDurationMs)
                    if (isPlayingNow && second > 0 && second / 15 != lastSavedSecond / 15) saveProgress()
                    lastSavedSecond = second
                }
                // Frequent checks plus a small look-ahead prevent brief events
                // from playing between coarse UI position updates.
                delay(if (isPlayingNow) 100 else 250)
            }
        }
    }

    fun open(book: LibraryBook, accessToken: String) {
        val previousBook = mutableState.value.book
        val previousToken = token
        if (previousBook != null && previousBook.id != book.id && previousToken != null) {
            checkpointProgress(
                book = previousBook,
                accessToken = previousToken,
                positionMs = currentPositionMs,
            )
        }

        // The playback service may already be holding this exact book, typically
        // after an Activity recreation while audio kept playing in the
        // background. Adopt that running playback instead of tearing it down,
        // which would stop the listener's audio the moment they reopen the app.
        val alreadyLoaded = controller?.let { active ->
            active.mediaItemCount > 0 && active.currentMediaItem?.mediaId == book.id
        } == true

        if (!alreadyLoaded) {
            // Detach the previous item immediately. This prevents a fast tap on Play
            // from starting the last audiobook while the new book's filters load.
            hasStartedPlayback = false
            controller?.pause()
            controller?.stop()
            controller?.clearMediaItems()
        }
        localProgress.edit().putString(LAST_BOOK_ID_KEY, book.id).apply()
        openJob?.cancel()
        openJob = viewModelScope.launch {
            token = accessToken
            val directlyMappedUri = localAudio.find(book.fingerprint.sha256)
            // A scanned catalog edition can have a canonical fingerprint that differs
            // from a locally converted/imported file. Recover that one-time mapping
            // by its embedded title, then persist it under the library fingerprint.
            val recovered = if (directlyMappedUri == null) localAudio.findLikelyMatch(book.title) else null
            if (directlyMappedUri == null && recovered != null) {
                localAudio.save(book.fingerprint.sha256, recovered.uri, recovered.chapters)
            }
            val uri = directlyMappedUri ?: recovered?.uri
            val chapters = if (directlyMappedUri != null) {
                localAudio.chapters(book.fingerprint.sha256)
            } else {
                recovered?.chapters.orEmpty()
            }
            val epub = localAudio.epub(book.fingerprint.sha256)
            // Prefer the cached extraction. Re-unzipping a novel on every open was
            // a multi-hundred-millisecond main-thread freeze plus a large
            // transient heap spike.
            val cachedEpubText = if (epub == null) null else localAudio.epubText(book.fingerprint.sha256)
            val readerTimingRanges = localAudio.epubAlignment(book.fingerprint.sha256)
            val embeddedCoverPath = uri?.let { localAudio.ensureCover(book.fingerprint.sha256, it) }
            val coverPath = embeddedCoverPath ?: book.coverImageURL?.let { coverURL ->
                runCatching {
                    localAudio.saveBookCover(
                        book.fingerprint.sha256,
                        api.downloadExploreCover(accessToken, coverURL),
                    )
                }.getOrNull()
            } ?: runCatching {
                // Converted imports can retain the title but lose the catalog
                // cover URL. Recover the published cover by edition title.
                val catalog = api.explore(accessToken).firstOrNull { item ->
                    val bookTitle = book.title.lowercase()
                    val itemTitle = item.title.lowercase()
                    bookTitle.contains("iron flame") && bookTitle.contains("part 2") &&
                        itemTitle.contains("iron flame") && itemTitle.contains("part 2")
                }
                val coverURL = catalog?.coverImageURL ?: return@runCatching null
                localAudio.saveBookCover(
                    book.fingerprint.sha256,
                    api.downloadExploreCover(accessToken, coverURL),
                )
            }.getOrNull()
            val resolvedEpubText = cachedEpubText
                ?: epub?.let { EpubTextReader.read(context.contentResolver, it) }
                    ?.takeIf(String::isNotBlank)
                    ?.also { localAudio.saveEpubText(book.fingerprint.sha256, it) }
            mutableState.value = PlayerUiState(
                book = book,
                localUri = uri,
                chapters = chapters,
                coverPath = coverPath,
                epubText = resolvedEpubText,
                readerParagraphs = paragraphsFor(resolvedEpubText),
                readerTimingRanges = readerTimingRanges,
                readerSettings = localAudio.readerSettings(),
                readerPosition = localAudio.readerPosition(book.fingerprint.sha256),
                speed = localAudio.playbackSpeed(book.fingerprint.sha256),
            )
            // Earlier Experimental builds could attach an EPUB before the
            // reader-sync endpoint existed. Retry automatically on opening the
            // book so users never have to remove and reattach their file.
            val epubText = mutableState.value.epubText
            val alignmentMatchesEpub = epubText?.let { localAudio.epubAlignmentMatches(book.fingerprint.sha256, it) } == true
            if (BuildConfig.BETA_BUILD && epubText != null && (!alignmentMatchesEpub || readerTimingRanges.isEmpty())) {
                syncReaderEdition(book, epubText, accessToken)
            }
            // A beta M4B may have been locally converted, so its file hash can differ
            // from the source edition's saved scan. Reopen the exact approved catalog
            // profile instead of treating playback as a fresh fingerprint lookup.
            val scanRequest = async {
                runCatching {
                    if (BetaConfig.enabled) {
                        val approved = BetaConfig.approvedEdition(book.fingerprint, api.explore(accessToken))
                        if (approved != null) api.exploreFilterResult(accessToken, approved.catalogBook.catalogID)
                        else api.findScan(accessToken, book.fingerprint)
                    } else {
                        api.findScan(accessToken, book.fingerprint)
                    }
                }.getOrNull()
            }
            val bookmarksRequest = async { runCatching { api.bookmarks(accessToken, book.id) }.getOrNull() }
            val settingsRequest = async {
                runCatching { api.bookFilterSettings(accessToken, book.id) }.getOrNull()
            }
            val cachedPlayback = localAudio.offlinePlayback(book.fingerprint.sha256)
            val scan = scanRequest.await()
            val events = (scan?.result?.events ?: cachedPlayback.events).filterNot(::isExcludedViolenceEvent)
            // A scan that was previously saved always carries a scanner version,
            // so this distinguishes "cached clean book" from "no scan at all".
            val hasCachedScan = cachedPlayback.scannerVersion != null
            val filterAvailability = when {
                scan?.result != null -> FilterAvailability.LIVE
                hasCachedScan -> FilterAvailability.CACHED
                else -> FilterAvailability.UNAVAILABLE
            }
            val remoteBookmarks = bookmarksRequest.await()
            val bookmarks = remoteBookmarks ?: cachedPlayback.bookmarks
            val cloudSettings = settingsRequest.await()
            val legacyDisabledGroups = if (cloudSettings == null) {
                localAudio.disabledFilters(book.fingerprint.sha256).ifEmpty { cachedPlayback.disabledGroupIDs.toSet() }
            } else emptySet()
            val disabledCategories = cloudSettings?.disabledCategoryIDs?.map { it.lowercase() }
                ?: cachedPlayback.disabledCategoryIDs.map { it.lowercase() }
            val disabledGroups = cloudSettings?.disabledGroupIDs?.map { it.lowercase() }
                ?: legacyDisabledGroups.toList()
            val disabledEvents = cloudSettings?.disabledEventKeys ?: cachedPlayback.disabledEventKeys
            val disabledAggregates = cloudSettings?.disabledAggregateKeys ?: cachedPlayback.disabledAggregateKeys
            localAudio.saveOfflinePlayback(
                book.fingerprint.sha256,
                OfflineBookPlayback(
                    scannerVersion = scan?.result?.scannerVersion ?: cachedPlayback.scannerVersion,
                    events = events,
                    bookmarks = bookmarks,
                    disabledCategoryIDs = disabledCategories,
                    disabledGroupIDs = disabledGroups,
                    disabledEventKeys = disabledEvents,
                    disabledAggregateKeys = disabledAggregates,
                ),
            )
            mutableState.value = mutableState.value.copy(
                scanEvents = events,
                scannerVersion = scan?.result?.scannerVersion ?: cachedPlayback.scannerVersion,
                filterAvailability = filterAvailability,
                bookmarks = bookmarks,
                disabledCategoryIDs = disabledCategories.toSet(),
                disabledGroupIDs = disabledGroups.toSet(),
                disabledEventKeys = disabledEvents.toSet(),
                disabledAggregateKeys = disabledAggregates.toSet(),
            )
            if (remoteBookmarks != null) {
                flushPendingBookmarks(book, accessToken)
            }
            if (cloudSettings != null && localAudio.filterSettingsDirty(book.fingerprint.sha256)) {
                syncPendingFilterSettings(book, accessToken)
            }
            if (uri == null) {
                return@launch
            }
            reportEditionSignature(book, uri, chapters, accessToken)
            // Wait for the playback service before touching the transport. The
            // connection is normally already up, but a cold start can open a
            // book before it completes.
            val active = connectedController.filterNotNull().first()
            if (mutableState.value.book?.id != book.id) return@launch
            if (alreadyLoaded) {
                // Audio is still running from the background session. Re-publish
                // the live transport state, which the fresh PlayerUiState above
                // reset, and leave the position untouched.
                trace(
                    book.id,
                    "open ADOPTED position=${active.currentPosition} playing=${active.isPlaying} " +
                        "state=${active.playbackState}",
                )
                // Adopting exists so reopening the app never interrupts audio that is
                // still playing in the background. It must not also adopt a position
                // from a player that is merely loaded and sitting idle: that is how a
                // resume position issued by an earlier open() got discarded, leaving
                // the book to play from the start.
                if (!active.isPlaying) {
                    val resumeMs = resumePositionMs(book)
                    pendingResumeMs = resumeMs.takeIf { it > RESUME_TOLERANCE_MS }
                    if (resumeMs > active.currentPosition + ADOPTED_POSITION_TOLERANCE_MS) {
                        active.seekTo(resumeMs)
                        trace(book.id, "open ADOPTED corrected to=$resumeMs")
                    }
                }
                if (active.isPlaying) hasStartedPlayback = true
                mutableState.value = mutableState.value.copy(
                    isPlaying = active.isPlaying,
                    isReady = active.playbackState == Player.STATE_READY,
                    positionMs = currentPositionMs,
                    durationMs = rawDurationMs.coerceAtLeast(0),
                )
                return@launch
            }
            // The start position is handed to setMediaItem rather than issued as a
            // separate seekTo. A MediaController forwards commands to the service over
            // IPC, so a seek after prepare() leaves a window in which the player still
            // reports 0 -- and a second open() arriving inside that window adopts the
            // 0 and plays from the beginning. Supplying it here makes the resume
            // position part of preparing, with no in-flight seek to lose.
            val resumeMs = resumePositionMs(book)
            // Recorded as the position playback must reach before it may begin, and
            // as the value a checkpoint must not overwrite until it has been reached.
            pendingResumeMs = resumeMs.takeIf { it > RESUME_TOLERANCE_MS }
            active.setMediaItem(mediaItemFor(book, uri, coverPath), resumeMs)
            // Applied before prepare so the book starts at the speed it was left at, rather
            // than a moment of normal speed followed by a jump once the UI catches up.
            active.setPlaybackSpeed(localAudio.playbackSpeed(book.fingerprint.sha256))
            active.prepare()
            trace(book.id, "open loaded startAt=$resumeMs after=${active.currentPosition}")
            // Filter state is loaded before the player becomes ready. Resuming,
            // scrubbing, chapter jumps and skip buttons therefore cannot begin
            // playback from inside an enabled filter window.
            enforceEnabledFilters(resumeMs, allowLookAhead = false)
            mutableState.value = mutableState.value.copy(
                positionMs = currentPositionMs,
            )
        }
    }

    /**
     * Reports the identity evidence for a book that was imported before signatures
     * existed, so edition matching can work for the library a listener already has.
     *
     * Hooked to opening a book rather than run as a library-wide sweep: it costs one
     * tag read on the file that is being opened anyway, and it covers exactly the
     * books someone actually listens to. Recorded once per file and never retried
     * after a successful report, and every failure is silent because this only
     * improves matching.
     */
    private fun reportEditionSignature(
        book: LibraryBook,
        uri: Uri,
        chapters: List<AudioChapter>,
        accessToken: String,
    ) {
        val reportedKey = "signature_reported_${book.fingerprint.sha256.lowercase()}"
        if (localProgress.getBoolean(reportedKey, false)) return
        viewModelScope.launch {
            val signature = withContext(Dispatchers.IO) {
                EditionSignatures.from(
                    tags = Mp4TagReader(context.contentResolver).read(uri),
                    chapters = chapters,
                )
            }
            // Nothing to say about this file, so stop looking at it on every open.
            if (signature == null) {
                localProgress.edit().putBoolean(reportedKey, true).apply()
                return@launch
            }
            val delivered = runCatching {
                api.reportEditionSignature(
                    accessToken,
                    EditionSignatureReportRequest(book.fingerprint, signature),
                )
            }.isSuccess
            if (delivered) localProgress.edit().putBoolean(reportedKey, true).apply()
        }
    }

    private fun isExcludedViolenceEvent(event: com.audiochoice.contracts.ScanEvent): Boolean {
        val category = event.categoryID.lowercase()
        val group = event.groupID.lowercase()
        return category.startsWith("30000000-") &&
            (group.endsWith("-000000000001") || group.endsWith("-000000000002") || group.endsWith("-000000000005"))
    }

    fun attachEpub(uri: Uri) {
        val book = mutableState.value.book ?: return
        viewModelScope.launch {
            runCatching { context.contentResolver.takePersistableUriPermission(uri, android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION) }
            localAudio.saveEpub(book.fingerprint.sha256, uri)
            val text = EpubTextReader.read(context.contentResolver, uri)
            if (text.isBlank()) {
                mutableState.value = mutableState.value.copy(error = "That EPUB could not be read.")
                return@launch
            }
            localAudio.saveEpubText(book.fingerprint.sha256, text)
            mutableState.value = mutableState.value.copy(
                epubText = text,
                readerParagraphs = paragraphsFor(text),
                readerTimingRanges = emptyList(),
            )
            token?.let { syncReaderEdition(book, text, it) }
        }
    }

    fun updateReaderSettings(settings: ReaderSettings) {
        mutableState.value = mutableState.value.copy(readerSettings = settings)
        viewModelScope.launch { localAudio.saveReaderSettings(settings) }
    }

    /**
     * Records the reading anchor so reopening the reader lands where the listener
     * left off, including after toggling out to the player and back.
     */
    fun saveReaderPosition(paragraphIndex: Int, scrollOffset: Int) {
        val book = mutableState.value.book ?: return
        val position = ReaderPosition(paragraphIndex.coerceAtLeast(0), scrollOffset.coerceAtLeast(0))
        if (mutableState.value.readerPosition == position) return
        mutableState.value = mutableState.value.copy(readerPosition = position)
        viewModelScope.launch { localAudio.saveReaderPosition(book.fingerprint.sha256, position) }
    }

    /** Removes the attached reading edition, leaving the audiobook untouched. */
    fun detachEpub() {
        val book = mutableState.value.book ?: return
        viewModelScope.launch {
            localAudio.removeEpub(book.fingerprint.sha256)
            mutableState.value = mutableState.value.copy(
                epubText = null,
                readerParagraphs = emptyList(),
                readerTimingRanges = emptyList(),
                readerSyncMessage = null,
                readerPosition = ReaderPosition(),
            )
        }
    }

    /**
     * Parses paragraph offsets off the main thread. A novel is a single pass over
     * roughly a megabyte, which is cheap but not free enough to do during
     * composition.
     */
    private suspend fun paragraphsFor(epubText: String?): List<ReaderParagraph> =
        if (epubText == null) {
            emptyList()
        } else {
            kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.Default) {
                ReaderParagraphParser.parse(epubText)
            }
        }

    /** Reader mode always refreshes from the current EPUB text, avoiding stale maps. */
    fun syncReaderEditionNow() {
        val book = mutableState.value.book ?: return
        val epubText = mutableState.value.epubText ?: return
        val accessToken = token ?: return
        viewModelScope.launch { syncReaderEdition(book, epubText, accessToken) }
    }

    private suspend fun syncReaderEdition(book: LibraryBook, epubText: String, accessToken: String) {
        mutableState.value = mutableState.value.copy(readerSyncMessage = "Syncing reading edition…")
        val outcome = runCatching { api.createReaderAlignment(accessToken, book.id, epubText) }
        val ranges = outcome.getOrNull()?.ranges.orEmpty()

        // Only cache a real answer. Caching a failure recorded "matched, zero
        // ranges", which made epubAlignmentMatches() report success and stopped
        // the sync from ever being retried on a later open.
        if (outcome.isSuccess) {
            localAudio.saveEpubAlignment(book.fingerprint.sha256, ranges, epubText)
        }

        val current = mutableState.value
        if (current.book?.id != book.id) return
        mutableState.value = current.copy(
            readerTimingRanges = ranges,
            readerSyncMessage = readerSyncMessageFor(outcome.exceptionOrNull(), ranges.size),
        )
    }

    /**
     * The previous single message ("Could not reach audiobook timing data")
     * collapsed a network failure, an expired session and "this book has no
     * transcript on the server" into one unactionable string.
     */
    private fun readerSyncMessageFor(failure: Throwable?, rangeCount: Int): String = when {
        failure is ApiException && failure.statusCode == 404 ->
            // The server's own wording is the useful part here, e.g. that no
            // private timing data exists for this audiobook yet.
            failure.message?.takeIf { it.isNotBlank() }
                ?: "This audiobook has no timing data on the server yet, so the reader cannot follow along."

        failure is ApiException && failure.statusCode == 400 ->
            "This reading edition is empty or too large to sync."

        failure is ApiException ->
            "Reading sync failed (${failure.statusCode}). ${failure.message.orEmpty()}".trim()

        failure != null ->
            "Could not reach AudioChoice to sync the reading edition. Check your connection, then tap Re-sync."

        rangeCount == 0 ->
            "This reading edition did not match the audiobook text, so the reader cannot follow along."

        else -> "Synced $rangeCount reading sections to the audiobook."
    }

    fun toggle() {
        if (!mutableState.value.isReady) return
        if (isPlayingNow) {
            controller?.pause()
        } else {
            enforceEnabledFilters(currentPositionMs, allowLookAhead = true)
            controller?.play()
        }
    }
    fun close() {
        saveProgress()
        controller?.stop()
        controller?.clearMediaItems()
        mutableState.value = PlayerUiState()
        token = null
    }
    fun start(fromBeginning: Boolean) {
        if (!mutableState.value.isReady) return
        if (fromBeginning) {
            pendingResumeMs = null
            seekTo(0)
        } else {
            // Never begin playback before the resume position has actually taken
            // effect. The transport lives in another process, so a start position or
            // seek can still be in flight when the player reports itself ready --
            // and starting in that window is what plays a book from the beginning.
            // Verifying rather than assuming makes this independent of *why* the
            // position had not been applied yet.
            pendingResumeMs?.let { target ->
                if (currentPositionMs + RESUME_TOLERANCE_MS < target) {
                    trace(
                        mutableState.value.book?.id.orEmpty(),
                        "start REAPPLIED target=$target was=$currentPositionMs",
                    )
                    controller?.seekTo(target)
                }
            }
            enforceEnabledFilters(currentPositionMs, allowLookAhead = true)
        }
        controller?.play()
    }

    fun openAndStart(book: LibraryBook, accessToken: String, fromBeginning: Boolean) {
        open(book, accessToken)
        viewModelScope.launch {
            while (isActive && mutableState.value.error == null &&
                (mutableState.value.book?.id != book.id || !mutableState.value.isReady)) delay(25)
            if (mutableState.value.book?.id == book.id && mutableState.value.localUri != null && mutableState.value.isReady) {
                start(fromBeginning)
            }
        }
    }
    fun seekTo(positionMs: Long) {
        if (!mutableState.value.isReady) return
        val duration = rawDurationMs.takeIf { it > 0 }
        val target = positionMs.coerceAtLeast(0).let { requested ->
            if (duration == null) requested else requested.coerceAtMost(duration)
        }
        pendingFilterSeekTargetMs = null
        // For a single-file book this resolves to the same seekTo(target) call as before.
        // For a narrated book the position has to be split into which chapter's file and
        // how far into it, which only the timeline knows.
        val seek = playbackTimeline.seekTarget(target)
        val itemIndex = seek.itemIndex
        if (itemIndex == null) controller?.seekTo(seek.positionMs)
        else controller?.seekTo(itemIndex, seek.positionMs)
        // The listener normally runs immediately, but this direct check also
        // protects same-position seeks that some devices may coalesce.
        enforceEnabledFilters(target, allowLookAhead = isPlayingNow)
    }
    fun skip(seconds: Int) { seekTo(currentPositionMs + seconds * 1000L) }
    fun previousChapter() {
        val position = currentPositionMs / 1000.0
        val currentIndex = mutableState.value.chapters.indexOfLast { it.startSeconds <= position }
        val target = if (currentIndex > 0) mutableState.value.chapters[currentIndex - 1] else null
        if (target != null) seekToChapter(target) else skip(-30)
    }

    fun nextChapter() {
        val position = currentPositionMs / 1000.0
        val target = mutableState.value.chapters.firstOrNull { it.startSeconds > position + 1.0 }
        if (target != null) seekToChapter(target) else skip(30)
    }
    fun setSpeed(speed: Float) {
        controller?.setPlaybackSpeed(speed)
        mutableState.value = mutableState.value.copy(speed = speed)
        // Remembered against this book, so returning to it keeps the chosen speed.
        val sha256 = mutableState.value.book?.fingerprint?.sha256 ?: return
        viewModelScope.launch { localAudio.savePlaybackSpeed(sha256, speed) }
    }

    fun setSleepTimer(minutes: Int?) {
        sleepJob?.cancel()
        sleepAtPositionMs = null
        if (minutes == null) {
            mutableState.value = mutableState.value.copy(sleepSecondsRemaining = null)
            return
        }
        mutableState.value = mutableState.value.copy(sleepSecondsRemaining = minutes * 60)
        sleepJob = viewModelScope.launch {
            while (isActive && (mutableState.value.sleepSecondsRemaining ?: 0) > 0) {
                delay(1_000)
                val remaining = (mutableState.value.sleepSecondsRemaining ?: 1) - 1
                mutableState.value = mutableState.value.copy(sleepSecondsRemaining = remaining)
            }
            controller?.pause()
            mutableState.value = mutableState.value.copy(sleepSecondsRemaining = null)
            saveProgress()
        }
    }

    fun sleepAtEndOfChapter() {
        sleepJob?.cancel()
        val position = currentPositionMs / 1000.0
        val chapter = mutableState.value.chapters.firstOrNull { position >= it.startSeconds && position < it.endSeconds }
            ?: return
        sleepAtPositionMs = (chapter.endSeconds * 1000).toLong()
        mutableState.value = mutableState.value.copy(
            sleepSecondsRemaining = (chapter.endSeconds - position).toInt().coerceAtLeast(1),
        )
    }

    fun seekToChapter(chapter: AudioChapter) = seekTo((chapter.startSeconds * 1000).toLong())

    fun addBookmark() {
        val book = mutableState.value.book ?: return
        val accessToken = token ?: return
        val position = currentPositionMs / 1000.0
        viewModelScope.launch {
            val pending = PendingBookmark(
                clientID = UUID.randomUUID().toString(),
                positionSeconds = position,
                createdAt = System.currentTimeMillis().toString(),
            )
            val localBookmark = LibraryBookmark(
                id = "local-${pending.clientID}",
                libraryBookID = book.id,
                positionSeconds = position,
                title = "Bookmark at ${formatBookmarkTime(position)}",
                createdAt = pending.createdAt,
                updatedAt = pending.createdAt,
            )
            localAudio.addPendingBookmark(book.fingerprint.sha256, pending)
            mutableState.value = mutableState.value.copy(
                bookmarks = (mutableState.value.bookmarks + localBookmark).sortedBy { it.positionSeconds },
                bookmarkSaved = true,
            )
            saveCurrentOfflinePlayback(book)
            flushPendingBookmarks(book, accessToken)
            delay(1_500)
            mutableState.value = mutableState.value.copy(bookmarkSaved = false)
        }
    }

    fun seekToBookmark(bookmark: LibraryBookmark) = seekTo((bookmark.positionSeconds * 1000).toLong())

    fun deleteBookmark(bookmark: LibraryBookmark) {
        val book = mutableState.value.book ?: return
        val accessToken = token ?: return
        viewModelScope.launch {
            if (bookmark.id.startsWith("local-")) {
                localAudio.removePendingBookmark(book.fingerprint.sha256, bookmark.id.removePrefix("local-"))
            } else {
                runCatching { api.deleteBookmark(accessToken, bookmark.id) }.getOrElse { return@launch }
            }
            mutableState.value = mutableState.value.copy(
                bookmarks = mutableState.value.bookmarks.filterNot { it.id == bookmark.id },
            )
            saveCurrentOfflinePlayback(book)
        }
    }

    fun isCategoryEnabled(id: String) = id.lowercase() !in mutableState.value.disabledCategoryIDs
    fun isGroupEnabled(id: String) = id.lowercase() !in mutableState.value.disabledGroupIDs
    fun isFilterEventEnabled(event: PlaybackFilterEvent) = if (event.aggregate)
        event.key !in mutableState.value.disabledAggregateKeys else event.key !in mutableState.value.disabledEventKeys

    fun setFilterCategory(categoryID: String, enabled: Boolean) {
        val current = mutableState.value
        val category = categoryID.lowercase()
        val parent = PlaybackFilterTaxonomy.available(current.scanEvents).firstOrNull { it.id == category }
        val categories = current.disabledCategoryIDs.toMutableSet()
        val groups = current.disabledGroupIDs.toMutableSet()
        val events = current.disabledEventKeys.toMutableSet()
        val aggregates = current.disabledAggregateKeys.toMutableSet()
        if (enabled) categories.remove(category) else categories.add(category)
        parent?.children?.forEach { child ->
            if (enabled) groups.remove(child.id) else groups.add(child.id)
            child.events.forEach { event ->
                val values = if (event.aggregate) aggregates else events
                if (enabled) values.remove(event.key) else values.add(event.key)
            }
        }
        mutableState.value = current.copy(
            disabledCategoryIDs = categories,
            disabledGroupIDs = groups,
            disabledEventKeys = events,
            disabledAggregateKeys = aggregates,
        )
        saveFilterChoices()
    }

    fun setFilterGroup(groupID: String, enabled: Boolean) {
        val current = mutableState.value
        val group = groupID.lowercase()
        val child = PlaybackFilterTaxonomy.available(current.scanEvents)
            .flatMap { it.children }.firstOrNull { it.id == group }
        val groups = current.disabledGroupIDs.toMutableSet()
        val events = current.disabledEventKeys.toMutableSet()
        val aggregates = current.disabledAggregateKeys.toMutableSet()
        if (enabled) groups.remove(group) else groups.add(group)
        child?.events?.forEach { event ->
            val values = if (event.aggregate) aggregates else events
            if (enabled) values.remove(event.key) else values.add(event.key)
        }
        mutableState.value = current.copy(
            disabledGroupIDs = groups,
            disabledEventKeys = events,
            disabledAggregateKeys = aggregates,
        )
        saveFilterChoices()
    }

    fun setFilterEvent(event: PlaybackFilterEvent, enabled: Boolean) {
        if (event.aggregate) {
            val values = mutableState.value.disabledAggregateKeys.toMutableSet()
            if (enabled) values.remove(event.key) else values.add(event.key)
            mutableState.value = mutableState.value.copy(disabledAggregateKeys = values)
        } else {
            val values = mutableState.value.disabledEventKeys.toMutableSet()
            if (enabled) values.remove(event.key) else values.add(event.key)
            mutableState.value = mutableState.value.copy(disabledEventKeys = values)
        }
        saveFilterChoices()
    }

    private fun saveFilterChoices() {
        val book = mutableState.value.book ?: return
        val value = mutableState.value
        viewModelScope.launch {
            localAudio.saveDisabledFilters(book.fingerprint.sha256, value.disabledGroupIDs)
            saveCurrentOfflinePlayback(book)
            localAudio.markFilterSettingsDirty(book.fingerprint.sha256, true)
            token?.let { accessToken ->
                runCatching { api.saveBookFilterSettings(
                    accessToken, book.id, BookFilterSettingsUpsertRequest(
                        value.disabledCategoryIDs.toList(), value.disabledGroupIDs.toList(),
                        value.disabledEventKeys.toList(), value.disabledAggregateKeys.toList(),
                    )
                ) }.onSuccess {
                    localAudio.markFilterSettingsDirty(book.fingerprint.sha256, false)
                }
            }
        }
    }

    private suspend fun saveCurrentOfflinePlayback(book: LibraryBook) {
        val value = mutableState.value
        localAudio.saveOfflinePlayback(
            book.fingerprint.sha256,
            OfflineBookPlayback(
                scannerVersion = value.scannerVersion,
                events = value.scanEvents,
                bookmarks = value.bookmarks,
                disabledCategoryIDs = value.disabledCategoryIDs.toList(),
                disabledGroupIDs = value.disabledGroupIDs.toList(),
                disabledEventKeys = value.disabledEventKeys.toList(),
                disabledAggregateKeys = value.disabledAggregateKeys.toList(),
            ),
        )
    }

    private suspend fun flushPendingBookmarks(book: LibraryBook, accessToken: String) {
        localAudio.pendingBookmarks(book.fingerprint.sha256).forEach { pending ->
            runCatching { api.addBookmark(accessToken, book.id, pending.positionSeconds) }
                .onSuccess { remote ->
                    localAudio.removePendingBookmark(book.fingerprint.sha256, pending.clientID)
                    mutableState.value = mutableState.value.copy(
                        bookmarks = (mutableState.value.bookmarks.filterNot {
                            it.id == "local-${pending.clientID}"
                        } + remote).distinctBy { it.id }.sortedBy { it.positionSeconds },
                    )
                    saveCurrentOfflinePlayback(book)
                }
        }
    }

    private suspend fun syncPendingFilterSettings(book: LibraryBook, accessToken: String) {
        val value = mutableState.value
        runCatching {
            api.saveBookFilterSettings(
                accessToken,
                book.id,
                BookFilterSettingsUpsertRequest(
                    value.disabledCategoryIDs.toList(), value.disabledGroupIDs.toList(),
                    value.disabledEventKeys.toList(), value.disabledAggregateKeys.toList(),
                ),
            )
        }.onSuccess {
            localAudio.markFilterSettingsDirty(book.fingerprint.sha256, false)
        }
    }

    private fun formatBookmarkTime(seconds: Double): String {
        val value = seconds.toLong().coerceAtLeast(0)
        return "%d:%02d:%02d".format(value / 3600, (value % 3600) / 60, value % 60)
    }

    fun saveProgress(onSaved: (() -> Unit)? = null) {
        val book = mutableState.value.book ?: return
        val accessToken = token ?: return
        checkpointProgress(book, accessToken, currentPositionMs, onSaved)
    }

    /**
     * Synchronous variant used by Activity.onStop() and onCleared(). Guarantees
     * the SharedPreferences write reaches disk before the process can be killed.
     * The network save still fires asynchronously, but the local checkpoint is
     * durable even under immediate process death.
     */
    // commit() is the point of this method: apply() is asynchronous and can lose
    // the write when the process is killed right after onStop.
    @SuppressLint("ApplySharedPref")
    fun saveProgressSync() {
        val book = mutableState.value.book
        val accessToken = token
        if (book == null || accessToken == null) {
            // A silent bail here would look identical to a save that wrote 0, so it
            // is recorded against the last opened book.
            lastOpenBookID()?.let { bookID ->
                trace(bookID, "saveSync SKIPPED book=${book != null} token=${accessToken != null}")
            }
            return
        }
        val trusted = trustedPositionMs
        val positionMs = trusted ?: lastKnownPositionMs
        // With no live transport this is a best-effort cached value, so it must
        // not be allowed to rewind a checkpoint that is already further along.
        // Deliberately only guards the untrusted path: a listener who really did
        // restart a book and closed the app still gets their 0 recorded.
        val storedBefore = localProgress.getLong(progressKey(book.id), -1L)
        if (trusted == null && storedBefore > positionMs) {
            trace(book.id, "saveSync BLOCKED trusted=null cached=$positionMs stored=$storedBefore")
            return
        }
        // Same protection as the periodic save: an outstanding resume means this
        // position is not where the listener actually was.
        pendingResumeMs?.let { target ->
            if (positionMs + RESUME_TOLERANCE_MS < target) {
                trace(book.id, "saveSync BLOCKED at=$positionMs pendingResume=$target")
                return
            }
            pendingResumeMs = null
        }
        trace(
            book.id,
            "saveSync wrote=$positionMs trusted=${trusted ?: -1} cached=$lastKnownPositionMs " +
                "stored=$storedBefore state=${controller?.playbackState ?: -1} " +
                "connected=${controller?.isConnected ?: false}",
        )
        val seconds = positionMs / 1000.0
        savedPositions[book.id] = seconds
        latestProgressMs[book.id] = positionMs
        localProgress.edit()
            .putLong(progressKey(book.id), positionMs)
            .putBoolean(progressDirtyKey(book.id), true)
            .putString(LAST_BOOK_ID_KEY, book.id)
            .commit()
        val mutex = progressSaveMutexes.getOrPut(book.id) { Mutex() }
        viewModelScope.launch {
            mutex.withLock {
                if (latestProgressMs[book.id] != positionMs) return@withLock
                runCatching {
                    // The stored value, never a default: the server assigns position and
                    // completion together, so sending false here would un-finish the book.
                    api.saveProgress(accessToken, book.id, seconds, isFinished(book.id))
                }
                    .onSuccess { savedBook ->
                        if (latestProgressMs[book.id] != positionMs) return@onSuccess
                        localProgress.edit()
                            .putLong(progressKey(book.id), positionMs)
                            .putBoolean(progressDirtyKey(book.id), false)
                            .apply()
                        val current = mutableState.value
                        if (current.book?.id == savedBook.id) {
                            mutableState.value = current.copy(book = savedBook)
                        }
                    }
            }
        }
    }

    /**
     * Reconciles crash/offline checkpoints as soon as the signed-in library is
     * loaded. Clean server values remain authoritative; only unsynced local
     * values are pushed back to the user's profile.
     */
    fun hydrateAccountProgress(
        books: List<LibraryBook>,
        accessToken: String,
        onPositionAvailable: (String, Double) -> Unit,
        onSynced: () -> Unit,
    ) {
        var pendingCount = 0
        books.forEach { book ->
            val localMs = localProgress.getLong(progressKey(book.id), -1L)
            // Versions before 1.3 wrote a local position without a dirty flag.
            // Treat that one-time legacy checkpoint as unsynced so installing
            // this fix does not discard the user's last known place.
            val dirty = localProgress.getBoolean(progressDirtyKey(book.id), false) ||
                (localMs >= 0L && !localProgress.contains(progressDirtyKey(book.id)))
            val serverMs = (book.playbackPositionSeconds.coerceAtLeast(0.0) * 1000.0).toLong()
            val hydrated = PlaybackResume.hydratedPositionMs(localMs, dirty, serverMs)

            trace(
                book.id,
                "hydrate chose=${hydrated.positionMs} local=$localMs dirty=$dirty server=$serverMs " +
                    "push=${hydrated.needsPush}",
            )
            savedPositions[book.id] = hydrated.positionMs / 1000.0
            onPositionAvailable(book.id, hydrated.positionMs / 1000.0)

            if (hydrated.needsPush) {
                pendingCount += 1
                checkpointProgress(book, accessToken, hydrated.positionMs) {
                    pendingCount -= 1
                    if (pendingCount == 0) onSynced()
                }
            } else {
                localProgress.edit()
                    .putLong(progressKey(book.id), hydrated.positionMs)
                    .putBoolean(progressDirtyKey(book.id), false)
                    .apply()
            }
        }
    }

    private fun checkpointProgress(
        book: LibraryBook,
        accessToken: String,
        positionMs: Long,
        onSaved: (() -> Unit)? = null,
    ) {
        val safePositionMs = positionMs.coerceAtLeast(0)
        // A resume that has not landed yet means the transport is reporting a position
        // the listener was never at. Saving it destroys the real checkpoint, which is
        // why a single failed resume used to become permanent: playback ran from the
        // start and the periodic save overwrote the good position within seconds.
        pendingResumeMs?.let { target ->
            if (safePositionMs + RESUME_TOLERANCE_MS < target) {
                trace(book.id, "checkpoint BLOCKED at=$safePositionMs pendingResume=$target")
                return
            }
            pendingResumeMs = null
        }
        trace(book.id, "checkpoint wrote=$safePositionMs")
        val seconds = safePositionMs / 1000.0
        savedPositions[book.id] = seconds
        latestProgressMs[book.id] = safePositionMs
        onSaved?.let { progressSaveCallbacks.getOrPut(book.id) { mutableListOf() }.add(it) }
        localProgress.edit()
            .putLong(progressKey(book.id), safePositionMs)
            .putBoolean(progressDirtyKey(book.id), true)
            .putString(LAST_BOOK_ID_KEY, book.id)
            .apply()
        val mutex = progressSaveMutexes.getOrPut(book.id) { Mutex() }
        viewModelScope.launch {
            mutex.withLock {
                // Coalesce frequent checkpoints for this book only. A save for
                // another audiobook can never cancel this one.
                if (latestProgressMs[book.id] != safePositionMs) return@withLock
                runCatching {
                    // The stored value, never a default: the server assigns position and
                    // completion together, so sending false here would un-finish the book.
                    api.saveProgress(accessToken, book.id, seconds, isFinished(book.id))
                }
                    .onSuccess { savedBook ->
                        if (latestProgressMs[book.id] != safePositionMs) return@onSuccess
                        localProgress.edit()
                            .putLong(progressKey(book.id), safePositionMs)
                            .putBoolean(progressDirtyKey(book.id), false)
                            .apply()
                        val current = mutableState.value
                        if (current.book?.id == savedBook.id) {
                            mutableState.value = current.copy(book = savedBook)
                        }
                        progressSaveCallbacks.remove(book.id)?.forEach { callback -> callback() }
                    }
                    .onFailure {
                        // The dirty flag stays set. Schedule a background drain so
                        // a failed save is not stranded until the next sign-in.
                        ProgressSyncWorker.enqueue(context)
                    }
            }
        }
    }

    /**
     * Reports that something played which should have been removed.
     *
     * One call, no arguments: whoever heard it is usually driving or walking, and anything
     * that needs answering means the report does not happen. The position is the tap and the
     * report carries a look-back window, because by now the passage is behind them.
     */
    fun reportMissedContent(categoryID: String? = null) {
        val current = mutableState.value
        val book = current.book ?: return
        val positionSeconds = (trustedPositionMs ?: lastKnownPositionMs) / 1000.0
        queueReport(
            FilterReportComposer.missedContent(
                fingerprint = book.fingerprint,
                positionSeconds = positionSeconds,
                scannerVersion = current.scannerVersion,
                categoryID = categoryID,
            ),
        )
    }

    /**
     * Reports that a control removed something it should not have.
     *
     * Resolved back to a scan event so the report names the control that fired, which is what
     * makes over-filtering correctable rather than just a complaint. An aggregate spans many
     * occurrences and has no single event, so that case reports the category and the first
     * range instead of inventing an identifier.
     */
    fun reportWronglyFiltered(controlKey: String, isAggregate: Boolean) {
        val current = mutableState.value
        val book = current.book ?: return
        val matches = current.scanEvents.filter { event ->
            if (isAggregate) event.aggregateKey == controlKey
            else event.stableKey.ifBlank { event.id } == controlKey
        }
        val first = matches.minByOrNull { it.startTime } ?: return
        queueReport(
            FilterReportComposer.wronglyFiltered(
                fingerprint = book.fingerprint,
                eventID = if (isAggregate) null else first.id,
                categoryID = first.categoryID,
                startSeconds = first.startTime,
                endSeconds = first.endTime,
                scannerVersion = current.scannerVersion,
            ),
        )
    }

    private fun queueReport(report: FilterReportRequest) {
        // Written to disk before anything is sent, so a report made with no signal survives.
        filterReports.enqueue(report)
        mutableState.value = mutableState.value.copy(filterReportSent = true)
        val accessToken = token ?: return
        viewModelScope.launch { filterReports.flush(accessToken) }
    }

    /** Clears the confirmation once the UI has shown it. */
    fun acknowledgeFilterReport() {
        mutableState.value = mutableState.value.copy(filterReportSent = false)
    }

    private fun progressKey(bookID: String): String = PlaybackProgressKeys.positionKey(bookID)
    private fun progressDirtyKey(bookID: String): String = PlaybackProgressKeys.dirtyKey(bookID)
    private fun progressFinishedKey(bookID: String): String =
        PlaybackProgressKeys.finishedKey(bookID)

    /**
     * Whether the open book is finished, as every save path needs to report it.
     *
     * Read from local storage rather than from the loaded book, because it is written the
     * moment playback reaches the end and the server copy may not have caught up.
     */
    private fun isFinished(bookID: String): Boolean =
        localProgress.getBoolean(
            progressFinishedKey(bookID),
            mutableState.value.book?.takeIf { it.id == bookID }?.isFinished ?: false,
        )

    /** Marks the open book finished once playback reaches the end. */
    private fun markFinishedIfAtEnd(positionMs: Long, durationMs: Long) {
        val book = mutableState.value.book ?: return
        // A narrated book's duration is only the chapters produced so far, so reaching
        // "the end" means the end of what exists, not the end of the book. Without this
        // a forty-chapter book would be marked finished on its third rendered chapter,
        // synced as finished, and -- because finishing clears the stored speed -- would
        // silently reset the listener's chosen playback speed too.
        mutableState.value.narration?.let { if (!it.fullyRendered) return }
        if (!BookCompletion.isComplete(positionMs, durationMs)) return
        // Guarded, or this would issue a save every hundred milliseconds through the outro.
        if (isFinished(book.id)) return
        setFinished(true)
    }

    /**
     * Records completion, on the device and for the account.
     *
     * Also how a listener marks a book they chose to stop before the end of, which is the
     * only way such a book can ever be finished.
     */
    fun setFinished(finished: Boolean) {
        val book = mutableState.value.book ?: return
        val accessToken = token ?: return
        localProgress.edit().putBoolean(progressFinishedKey(book.id), finished).apply()
        mutableState.value = mutableState.value.copy(book = book.copy(isFinished = finished))
        // A finished book starts over at normal speed. The chosen speed suited getting
        // through this narrator once; it should not silently govern a re-listen.
        if (finished) {
            viewModelScope.launch { localAudio.clearPlaybackSpeed(book.fingerprint.sha256) }
        }
        val seconds = (trustedPositionMs ?: lastKnownPositionMs) / 1000.0
        viewModelScope.launch {
            runCatching { api.saveProgress(accessToken, book.id, seconds, finished) }
                .onSuccess { saved ->
                    val current = mutableState.value
                    if (current.book?.id == saved.id) {
                        mutableState.value = current.copy(book = saved)
                    }
                }
        }
    }

    /**
     * Appends a line to this book's persisted progress trace.
     *
     * Position loss only shows up across a process restart, which is precisely when
     * a tester cannot read logs. Three attempts at fixing this were made by reasoning
     * about the code and all three missed, so the inputs are recorded instead of
     * inferred. Written with commit() because the interesting case is the app being
     * killed immediately afterwards.
     */
    @SuppressLint("ApplySharedPref")
    private fun trace(bookID: String, line: String) {
        val key = PlaybackProgressKeys.traceKey(bookID)
        val existing = localProgress.getString(key, "").orEmpty()
        // Keep only the last few entries; this is a diagnostic, not a log file.
        val kept = (existing.lines() + line).filter { it.isNotBlank() }.takeLast(12)
        localProgress.edit().putString(key, kept.joinToString("\n")).commit()
    }

    /** The recorded trace for a book, newest last. */
    fun progressTrace(bookID: String): String =
        localProgress.getString(PlaybackProgressKeys.traceKey(bookID), null)
            ?: "No progress activity recorded yet for this book."

    fun clearProgressTrace(bookID: String) {
        localProgress.edit().remove(PlaybackProgressKeys.traceKey(bookID)).apply()
    }

    private fun resumePositionMs(book: LibraryBook): Long {
        val localMs = localProgress.getLong(progressKey(book.id), -1L)
        val localIsDirty = localProgress.getBoolean(progressDirtyKey(book.id), false)
        val positionMs = PlaybackResume.resumePositionMs(
            sessionPositionMs = savedPositions[book.id]?.let { (it * 1000.0).toLong() },
            localPositionMs = localMs,
            localIsDirty = localIsDirty,
            serverPositionMs = (book.playbackPositionSeconds.coerceAtLeast(0.0) * 1000.0).toLong(),
        )
        trace(
            book.id,
            "resume chose=$positionMs session=${savedPositions[book.id]?.let { (it * 1000.0).toLong() } ?: -1} " +
                "local=$localMs dirty=$localIsDirty server=${(book.playbackPositionSeconds * 1000.0).toLong()}",
        )
        if (!localIsDirty) {
            localProgress.edit().putLong(progressKey(book.id), positionMs).apply()
        }
        return positionMs
    }

    private fun enforceEnabledFilters(positionMs: Long, allowLookAhead: Boolean) {
        val current = mutableState.value
        // A narrated book has nothing to skip: filtered passages were removed before the
        // text ever reached a voice, so they are absent from the audio rather than
        // present and skipped over.
        //
        // This guard is not an optimisation. A narrated book's ScanEvent startTime and
        // endTime carry character offsets into Book_Text, not seconds, which is what
        // lets the whole existing filter stack be reused unchanged. Handed to
        // FilterSkipPlanner they are read as seconds, and because a novel's filtered
        // passages tile its text, adjacent windows chain through the connected-block
        // expansion and the chain runs the length of the book: a seek of tens of hours
        // from the first minute of playback. NarrationPlaybackGuardTest demonstrates it.
        //
        // The test is on narration state rather than on an empty event list, because a
        // narrated book's event list is normally not empty.
        if (current.narration != null) return
        if (current.scanEvents.isEmpty()) return

        val enabledWindows = current.enabledScanEvents()
            .map { FilterWindow(it.startTime, it.endTime) }

        val positionSeconds = positionMs.coerceAtLeast(0) / 1000.0
        val targetSeconds = FilterSkipPlanner.targetSeconds(
            positionSeconds = positionSeconds,
            windows = enabledWindows,
            lookAheadSeconds = if (allowLookAhead) FILTER_LOOK_AHEAD_SECONDS else 0.0,
        ) ?: run {
            pendingFilterSeekTargetMs = null
            return
        }

        val durationMs = rawDurationMs.takeIf { it > 0 } ?: Long.MAX_VALUE
        val targetMs = ((targetSeconds + FILTER_EXIT_PADDING_SECONDS) * 1000.0)
            .toLong()
            .coerceAtMost(durationMs)
        if (targetMs <= positionMs + FILTER_SEEK_TOLERANCE_MS) return
        if (pendingFilterSeekTargetMs == targetMs) return

        pendingFilterSeekTargetMs = targetMs
        controller?.seekTo(targetMs)
    }

    private companion object {
        const val FILTER_LOOK_AHEAD_SECONDS = 0.25
        const val FILTER_EXIT_PADDING_SECONDS = 0.20
        const val FILTER_SEEK_TOLERANCE_MS = 25L

        /**
         * How far behind the saved position an idle adopted player may sit before the
         * resume position is reapplied. Wide enough not to fight a listener who nudged
         * the scrubber, narrow enough to catch a discarded resume.
         */
        const val ADOPTED_POSITION_TOLERANCE_MS = 5_000L

        /**
         * How close the transport must be to the intended resume position before it
         * counts as applied. Also the margin that protects a saved checkpoint from
         * being overwritten while a resume is still outstanding.
         */
        const val RESUME_TOLERANCE_MS = 3_000L
        val LAST_BOOK_ID_KEY = PlaybackProgressKeys.LAST_BOOK_ID
    }


    override fun onCleared() {
        // Save while the controller is still connected, then release only the
        // controller. The service keeps the ExoPlayer alive so audio continues
        // while the app is backgrounded; it stops itself when nothing is playing
        // and the task is removed.
        saveProgressSync()
        // viewModelScope is already cancelled here, so the network save launched
        // by saveProgressSync cannot run. WorkManager drains the local checkpoint
        // instead, surviving both this teardown and process death.
        ProgressSyncWorker.enqueue(context)
        controller?.removeListener(playerListener)
        controllerFuture?.let { MediaController.releaseFuture(it) }
        controllerFuture = null
        controller = null
        connectedController.value = null
    }

    /**
     * Supplies the title, author and artwork that the system media notification
     * and lock screen display. This replaces the old beta-only
     * PlayerNotificationManager: Media3's MediaSessionService renders the
     * notification from MediaMetadata for every build variant.
     */
    private fun mediaItemFor(book: LibraryBook, uri: Uri, coverPath: String?): MediaItem =
        MediaItem.Builder()
            .setUri(uri)
            // Lets open() recognise a book the service is already playing.
            .setMediaId(book.id)
            .setMediaMetadata(
                MediaMetadata.Builder()
                    .setTitle(book.title)
                    .setArtist(book.author)
                    .setArtworkUri(
                        coverPath?.let { path ->
                            java.io.File(path).takeIf(java.io.File::isFile)?.let(Uri::fromFile)
                        },
                    )
                    .setIsBrowsable(false)
                    .setIsPlayable(true)
                    .build(),
            )
            .build()

    class Factory(context: Context, private val api: AudioChoiceApi, private val localAudio: LocalAudioStore) : ViewModelProvider.Factory {
        private val appContext = context.applicationContext
        @Suppress("UNCHECKED_CAST")
        override fun <T : ViewModel> create(modelClass: Class<T>): T = PlayerViewModel(appContext, api, localAudio) as T
    }
}
