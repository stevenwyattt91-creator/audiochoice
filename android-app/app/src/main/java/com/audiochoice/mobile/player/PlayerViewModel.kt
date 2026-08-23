package com.audiochoice.mobile.player

import android.content.Context
import android.net.Uri
import android.content.SharedPreferences
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.exoplayer.ExoPlayer
import com.audiochoice.mobile.BuildConfig
import com.audiochoice.mobile.beta.BetaConfig
import com.audiochoice.mobile.beta.BetaPlaybackControls
import com.audiochoice.mobile.data.AudioChoiceApi
import com.audiochoice.mobile.data.AudioChapter
import com.audiochoice.mobile.data.LibraryBook
import com.audiochoice.mobile.data.LibraryBookmark
import com.audiochoice.mobile.data.BookFilterSettingsUpsertRequest
import com.audiochoice.mobile.data.LocalAudioStore
import com.audiochoice.mobile.data.OfflineBookPlayback
import com.audiochoice.mobile.data.PendingBookmark
import com.audiochoice.mobile.data.ReaderTimingRange
import com.audiochoice.mobile.reader.EpubTextReader
import com.audiochoice.contracts.ScanEvent
import kotlinx.coroutines.delay
import kotlinx.coroutines.async
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.util.UUID

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
    val disabledCategoryIDs: Set<String> = emptySet(),
    val disabledGroupIDs: Set<String> = emptySet(),
    val disabledEventKeys: Set<String> = emptySet(),
    val disabledAggregateKeys: Set<String> = emptySet(),
    val bookmarkSaved: Boolean = false,
    val error: String? = null,
    val epubText: String? = null,
    val readerTimingRanges: List<ReaderTimingRange> = emptyList(),
    val readerSyncMessage: String? = null,
    val isReady: Boolean = false,
)

class PlayerViewModel(
    private val context: Context,
    private val api: AudioChoiceApi,
    private val localAudio: LocalAudioStore,
) : ViewModel() {
    private val player = ExoPlayer.Builder(context)
        .setSeekBackIncrementMs(30_000)
        .setSeekForwardIncrementMs(30_000)
        .build()
    private val betaPlaybackControls = if (BuildConfig.BETA_BUILD) {
        BetaPlaybackControls(context, player)
    } else null
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
    private val localProgress: SharedPreferences = context.getSharedPreferences("audiochoice_playback", Context.MODE_PRIVATE)

    init {
        player.addListener(object : Player.Listener {
            override fun onIsPlayingChanged(isPlaying: Boolean) {
                mutableState.value = mutableState.value.copy(isPlaying = isPlaying)
                if (isPlaying) {
                    hasStartedPlayback = true
                    enforceEnabledFilters(player.currentPosition, allowLookAhead = true)
                } else if (hasStartedPlayback) {
                    // Pausing, an interruption, or the app moving to the
                    // background must retain the exact last listening point.
                    saveProgress()
                }
            }

            override fun onPlaybackStateChanged(playbackState: Int) {
                mutableState.value = mutableState.value.copy(
                    isReady = playbackState == Player.STATE_READY,
                    durationMs = player.duration.coerceAtLeast(0),
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
                enforceEnabledFilters(newPosition.positionMs, allowLookAhead = player.isPlaying)
            }
        })
        viewModelScope.launch {
            while (isActive) {
                if (mutableState.value.book != null) {
                    mutableState.value = mutableState.value.copy(
                        positionMs = player.currentPosition.coerceAtLeast(0),
                        durationMs = player.duration.coerceAtLeast(0),
                    )
                    val second = player.currentPosition / 1000
                    enforceEnabledFilters(player.currentPosition, allowLookAhead = player.isPlaying)
                    sleepAtPositionMs?.let { target ->
                        if (player.isPlaying && player.currentPosition >= target) {
                            player.pause()
                            sleepAtPositionMs = null
                            mutableState.value = mutableState.value.copy(sleepSecondsRemaining = null)
                            saveProgress()
                        } else {
                            mutableState.value = mutableState.value.copy(
                                sleepSecondsRemaining = ((target - player.currentPosition).coerceAtLeast(0) / 1000).toInt(),
                            )
                        }
                    }
                    if (player.isPlaying && second > 0 && second / 15 != lastSavedSecond / 15) saveProgress()
                    lastSavedSecond = second
                }
                // Frequent checks plus a small look-ahead prevent brief events
                // from playing between coarse UI position updates.
                delay(if (player.isPlaying) 100 else 250)
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
                positionMs = player.currentPosition.coerceAtLeast(0),
            )
        }

        // Detach the previous item immediately. This prevents a fast tap on Play
        // from starting the last audiobook while the new book's filters load.
        hasStartedPlayback = false
        player.pause()
        player.stop()
        player.clearMediaItems()
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
            betaPlaybackControls?.updateMetadata(book.title, book.author, coverPath)
            mutableState.value = PlayerUiState(
                book = book,
                localUri = uri,
                chapters = chapters,
                coverPath = coverPath,
                epubText = epub?.let { EpubTextReader.read(context.contentResolver, it) }?.takeIf(String::isNotBlank),
                readerTimingRanges = readerTimingRanges,
            )
            // Earlier Experimental builds could attach an EPUB before the
            // reader-sync endpoint existed. Retry automatically on opening the
            // book so users never have to remove and reattach their file.
            val epubText = mutableState.value.epubText
            val alignmentMatchesEpub = epubText?.let { localAudio.epubAlignmentMatches(book.fingerprint.sha256, it) } == true
            if (BuildConfig.EXPERIMENTAL_BUILD && epubText != null && (!alignmentMatchesEpub || readerTimingRanges.isEmpty())) {
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
            player.setMediaItem(MediaItem.fromUri(uri))
            player.prepare()
            val resumeMs = resumePositionMs(book)
            player.seekTo(resumeMs)
            // Filter state is loaded before the player becomes ready. Resuming,
            // scrubbing, chapter jumps and skip buttons therefore cannot begin
            // playback from inside an enabled filter window.
            enforceEnabledFilters(resumeMs, allowLookAhead = false)
            mutableState.value = mutableState.value.copy(
                positionMs = player.currentPosition.coerceAtLeast(0),
            )
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
            mutableState.value = mutableState.value.copy(epubText = text, readerTimingRanges = emptyList())
            token?.let { syncReaderEdition(book, text, it) }
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
        val alignment = runCatching { api.createReaderAlignment(accessToken, book.id, epubText) }.getOrNull()
        val ranges = alignment?.ranges.orEmpty()
        localAudio.saveEpubAlignment(book.fingerprint.sha256, ranges, epubText)
        val current = mutableState.value
        if (current.book?.id != book.id) return
        mutableState.value = current.copy(
            readerTimingRanges = ranges,
            readerSyncMessage = when {
                alignment == null -> "Could not reach audiobook timing data."
                ranges.isEmpty() -> "This reading edition did not match the audiobook text."
                else -> "Synced ${ranges.size} reading sections to the audiobook."
            },
        )
    }

    fun toggle() {
        if (!mutableState.value.isReady) return
        if (player.isPlaying) {
            player.pause()
        } else {
            enforceEnabledFilters(player.currentPosition, allowLookAhead = true)
            player.play()
        }
    }
    fun close() {
        saveProgress()
        player.stop()
        player.clearMediaItems()
        mutableState.value = PlayerUiState()
        token = null
    }
    fun start(fromBeginning: Boolean) {
        if (!mutableState.value.isReady) return
        if (fromBeginning) seekTo(0) else enforceEnabledFilters(player.currentPosition, allowLookAhead = true)
        player.play()
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
        val duration = player.duration.takeIf { it > 0 }
        val target = positionMs.coerceAtLeast(0).let { requested ->
            if (duration == null) requested else requested.coerceAtMost(duration)
        }
        pendingFilterSeekTargetMs = null
        player.seekTo(target)
        // The listener normally runs immediately, but this direct check also
        // protects same-position seeks that some devices may coalesce.
        enforceEnabledFilters(target, allowLookAhead = player.isPlaying)
    }
    fun skip(seconds: Int) { seekTo(player.currentPosition + seconds * 1000L) }
    fun previousChapter() {
        val position = player.currentPosition / 1000.0
        val currentIndex = mutableState.value.chapters.indexOfLast { it.startSeconds <= position }
        val target = if (currentIndex > 0) mutableState.value.chapters[currentIndex - 1] else null
        if (target != null) seekToChapter(target) else skip(-30)
    }

    fun nextChapter() {
        val position = player.currentPosition / 1000.0
        val target = mutableState.value.chapters.firstOrNull { it.startSeconds > position + 1.0 }
        if (target != null) seekToChapter(target) else skip(30)
    }
    fun setSpeed(speed: Float) { player.setPlaybackSpeed(speed); mutableState.value = mutableState.value.copy(speed = speed) }

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
            player.pause()
            mutableState.value = mutableState.value.copy(sleepSecondsRemaining = null)
            saveProgress()
        }
    }

    fun sleepAtEndOfChapter() {
        sleepJob?.cancel()
        val position = player.currentPosition / 1000.0
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
        val position = player.currentPosition.coerceAtLeast(0) / 1000.0
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
        checkpointProgress(book, accessToken, player.currentPosition.coerceAtLeast(0), onSaved)
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
            if (dirty && localMs >= 0L) {
                onPositionAvailable(book.id, localMs / 1000.0)
                pendingCount += 1
                checkpointProgress(book, accessToken, localMs) {
                    pendingCount -= 1
                    if (pendingCount == 0) onSynced()
                }
            } else {
                val serverMs = (book.playbackPositionSeconds.coerceAtLeast(0.0) * 1000.0).toLong()
                savedPositions[book.id] = serverMs / 1000.0
                localProgress.edit()
                    .putLong(progressKey(book.id), serverMs)
                    .putBoolean(progressDirtyKey(book.id), false)
                    .apply()
                onPositionAvailable(book.id, serverMs / 1000.0)
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
        val seconds = safePositionMs / 1000.0
        savedPositions[book.id] = seconds
        latestProgressMs[book.id] = safePositionMs
        onSaved?.let { progressSaveCallbacks.getOrPut(book.id) { mutableListOf() }.add(it) }
        localProgress.edit()
            .putLong(progressKey(book.id), safePositionMs)
            .putBoolean(progressDirtyKey(book.id), true)
            .apply()
        val mutex = progressSaveMutexes.getOrPut(book.id) { Mutex() }
        viewModelScope.launch {
            mutex.withLock {
                // Coalesce frequent checkpoints for this book only. A save for
                // another audiobook can never cancel this one.
                if (latestProgressMs[book.id] != safePositionMs) return@withLock
                runCatching { api.saveProgress(accessToken, book.id, seconds, false) }
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
            }
        }
    }

    private fun progressKey(bookID: String): String = "position_ms_$bookID"
    private fun progressDirtyKey(bookID: String): String = "position_dirty_$bookID"

    private fun resumePositionMs(book: LibraryBook): Long {
        val localMs = localProgress.getLong(progressKey(book.id), -1L)
        val localIsDirty = localProgress.getBoolean(progressDirtyKey(book.id), false)
        val serverMs = (book.playbackPositionSeconds.coerceAtLeast(0.0) * 1000.0).toLong()
        val positionMs = when {
            savedPositions.containsKey(book.id) -> (savedPositions.getValue(book.id) * 1000.0).toLong()
            localIsDirty && localMs >= 0L -> localMs
            else -> serverMs
        }.coerceAtLeast(0L)
        if (!localIsDirty) {
            localProgress.edit().putLong(progressKey(book.id), positionMs).apply()
        }
        return positionMs
    }

    private fun enforceEnabledFilters(positionMs: Long, allowLookAhead: Boolean) {
        val current = mutableState.value
        if (current.scanEvents.isEmpty()) return

        val enabledWindows = current.scanEvents.asSequence()
            .filter { event ->
                event.categoryID.lowercase() !in current.disabledCategoryIDs &&
                    event.groupID.lowercase() !in current.disabledGroupIDs &&
                    event.stableKey.ifBlank { event.id } !in current.disabledEventKeys &&
                    (event.aggregateKey == null || event.aggregateKey !in current.disabledAggregateKeys)
            }
            .map { FilterWindow(it.startTime, it.endTime) }
            .toList()

        val positionSeconds = positionMs.coerceAtLeast(0) / 1000.0
        val targetSeconds = FilterSkipPlanner.targetSeconds(
            positionSeconds = positionSeconds,
            windows = enabledWindows,
            lookAheadSeconds = if (allowLookAhead) FILTER_LOOK_AHEAD_SECONDS else 0.0,
        ) ?: run {
            pendingFilterSeekTargetMs = null
            return
        }

        val durationMs = player.duration.takeIf { it > 0 } ?: Long.MAX_VALUE
        val targetMs = ((targetSeconds + FILTER_EXIT_PADDING_SECONDS) * 1000.0)
            .toLong()
            .coerceAtMost(durationMs)
        if (targetMs <= positionMs + FILTER_SEEK_TOLERANCE_MS) return
        if (pendingFilterSeekTargetMs == targetMs) return

        pendingFilterSeekTargetMs = targetMs
        player.seekTo(targetMs)
    }

    private companion object {
        const val FILTER_LOOK_AHEAD_SECONDS = 0.25
        const val FILTER_EXIT_PADDING_SECONDS = 0.20
        const val FILTER_SEEK_TOLERANCE_MS = 25L
    }


    override fun onCleared() {
        saveProgress()
        betaPlaybackControls?.release()
        player.release()
    }

    class Factory(context: Context, private val api: AudioChoiceApi, private val localAudio: LocalAudioStore) : ViewModelProvider.Factory {
        private val appContext = context.applicationContext
        @Suppress("UNCHECKED_CAST")
        override fun <T : ViewModel> create(modelClass: Class<T>): T = PlayerViewModel(appContext, api, localAudio) as T
    }
}
