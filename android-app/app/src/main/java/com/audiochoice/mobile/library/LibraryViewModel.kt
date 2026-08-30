package com.audiochoice.mobile.library

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import com.audiochoice.mobile.data.AudioChoiceApi
import com.audiochoice.mobile.data.LibraryBook
import com.audiochoice.mobile.data.LibraryBookDetailsRequest
import com.audiochoice.mobile.data.LibraryBookUpsertRequest
import com.audiochoice.mobile.data.ExploreCatalogBook
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

data class LibraryUiState(
    val loading: Boolean = false,
    val loaded: Boolean = false,
    val books: List<LibraryBook> = emptyList(),
    val exploreBooks: List<ExploreCatalogBook> = emptyList(),
    val coverPaths: Map<String, String> = emptyMap(),
    /**
     * Ebooks with no filter results, by lowercase sha256.
     *
     * Surfaced in the list because a book that cannot be filtered is a book the listener has a
     * decision to make about, and finding that out only after opening it and pressing Read aloud
     * wastes the trip. Excludes books whose owner already chose to continue without results: they
     * have made the decision, and repeating it reads as nagging.
     */
    val ebooksWithoutFilterResults: Set<String> = emptySet(),
    val error: String? = null,
)

class LibraryViewModel(
    /**
     * The application context, for reading tags back out of stored files. Deliberately not
     * an activity, which a view model outlives.
     */
    private val context: android.content.Context,
    private val api: AudioChoiceApi,
    private val localAudio: com.audiochoice.mobile.data.LocalAudioStore,
) : ViewModel() {
    private val mutableState = MutableStateFlow(LibraryUiState())
    val state: StateFlow<LibraryUiState> = mutableState.asStateFlow()
    private var loadedForToken: String? = null

    fun updatePlaybackPosition(bookID: String, positionSeconds: Double) {
        mutableState.value = mutableState.value.copy(
            books = mutableState.value.books.map { book ->
                if (book.id == bookID) book.copy(playbackPositionSeconds = positionSeconds) else book
            },
        )
    }

    /**
     * Corrects a book's display details.
     *
     * Only the row a listener sees changes. The edition fingerprint is untouched, so
     * identification keeps working from the file's own metadata rather than from
     * typed-in text.
     */
    fun updateDetails(
        accessToken: String,
        book: LibraryBook,
        title: String,
        author: String?,
        narrator: String?,
        onComplete: (LibraryBook) -> Unit = {},
    ) {
        viewModelScope.launch {
            runCatching {
                api.updateBookDetails(
                    accessToken,
                    book.id,
                    LibraryBookDetailsRequest(
                        title = title.trim(),
                        author = author?.trim()?.takeIf(String::isNotBlank),
                        narrator = narrator?.trim()?.takeIf(String::isNotBlank),
                    ),
                )
            }.onSuccess { saved ->
                mutableState.value = mutableState.value.copy(
                    books = mutableState.value.books.map { existing ->
                        if (existing.id == saved.id) saved else existing
                    },
                )
                onComplete(saved)
            }.onFailure { error ->
                mutableState.value = mutableState.value.copy(error = error.message)
            }
        }
    }

    fun delete(accessToken: String, book: LibraryBook, onComplete: () -> Unit = {}) {
        viewModelScope.launch {
            runCatching {
                api.deleteBook(accessToken, book.id)
                localAudio.remove(book.fingerprint.sha256)
            }.onSuccess {
                mutableState.value = mutableState.value.copy(
                    books = mutableState.value.books.filterNot { it.id == book.id },
                )
                onComplete()
            }.onFailure { error ->
                mutableState.value = mutableState.value.copy(error = error.message)
            }
        }
    }

    fun load(accessToken: String, accountID: String, force: Boolean = false) {
        if (!force && loadedForToken == accessToken) return
        loadedForToken = accessToken
        viewModelScope.launch {
            mutableState.value = mutableState.value.copy(loading = true, error = null)
            val cachedBooks = localAudio.librarySnapshot(accountID)
            if (cachedBooks.isNotEmpty()) {
                mutableState.value = mutableState.value.copy(
                    loading = true,
                    loaded = true,
                    books = cachedBooks,
                )
            }
            runCatching {
                // Never mutate Library records during a normal refresh/import.
                // The prior one-time cleanup was intentionally removed because a
                // newly imported Iron Flame edition must remain in the Library.
                val serverBooks = api.library(accessToken)
                val initialExplore = hideConfirmedIronFlameCatalogDuplicate(api.explore(accessToken))
                // Repair older imports without forcing the user to re-import them.
                // The local cover is the offline source of truth; the backend stores
                // it against the exact edition fingerprint for future devices.
                serverBooks.forEach { book ->
                    if (book.coverImageURL == null) {
                        localAudio.coverBytes(book.fingerprint.sha256)?.let { bytes ->
                            runCatching {
                                api.uploadEmbeddedCover(accessToken, book.fingerprint, bytes)
                            }
                        }
                    }
                }
                reportMissingDescriptions(accessToken, serverBooks, initialExplore)
                // Resolve catalog artwork before enriching the user's books. If this is
                // the first import to supply artwork, every matching library record can
                // receive the same permanent server URL during this load.
                val explore = initialExplore.map { item ->
                    val ownedBooks = serverBooks.filter { it.matchesCatalog(item) }
                    var coverURL = item.coverImageURL
                    var coverBytes = coverURL?.let { path ->
                        runCatching { api.downloadExploreCover(accessToken, path) }.getOrNull()
                    } ?: localAudio.catalogCoverBytes(item.catalogID)

                    if (coverURL == null) {
                        coverBytes = coverBytes ?: ownedBooks.firstNotNullOfOrNull { owned ->
                            localAudio.coverBytes(owned.fingerprint.sha256)
                        }
                        if (coverBytes != null) {
                            val uploaded = runCatching {
                                api.uploadExploreCover(accessToken, item.catalogID, coverBytes)
                            }.getOrDefault(false)
                            if (uploaded) coverURL = "/v1/explore/${item.catalogID}/cover"
                        }
                    }

                    if (coverBytes != null) {
                        localAudio.saveCatalogCover(item.catalogID, coverBytes)
                        ownedBooks.forEach { owned ->
                            localAudio.saveBookCover(owned.fingerprint.sha256, coverBytes)
                        }
                    }
                    item.copy(coverImageURL = coverURL)
                }
                val enriched = serverBooks.map { book -> enrichBook(accessToken, book, explore) }

                // A library book can retain its cover even before its scan is published
                // in Explore. Cache that account-level URL for details and player use.
                enriched.forEach { book ->
                    if (localAudio.coverPath(book.fingerprint.sha256) == null && book.coverImageURL != null) {
                        runCatching {
                            localAudio.saveBookCover(
                                book.fingerprint.sha256,
                                api.downloadExploreCover(accessToken, book.coverImageURL),
                            )
                        }
                    }
                }
                val covers = enriched.mapNotNull { book ->
                    localAudio.coverPath(book.fingerprint.sha256)?.let { path ->
                        book.fingerprint.sha256.lowercase() to path
                    }
                }.toMap().toMutableMap()
                explore.forEach { item ->
                    val ownedBooks = enriched.filter { it.matchesCatalog(item) }
                    val cached = localAudio.catalogCoverPath(item.catalogID)
                    if (cached != null) {
                        covers[item.catalogID.lowercase()] = cached
                        ownedBooks.forEach { owned ->
                            val persistent = localAudio.coverPath(owned.fingerprint.sha256) ?: cached
                            covers[owned.fingerprint.sha256.lowercase()] = persistent
                        }
                    }
                }
                localAudio.saveLibrarySnapshot(accountID, enriched)
                Triple(enriched, explore, covers.toMap())
            }.onSuccess {
                mutableState.value = LibraryUiState(
                    loaded = true,
                    books = it.first,
                    exploreBooks = it.second,
                    coverPaths = it.third,
                )
                // After the list, not as part of it. A book's filter state is worth showing but not
                // worth delaying the whole library for, and a failure to read it must leave the
                // library intact rather than empty.
                refreshEbookFilterState(it.first)
            }
                .onFailure {
                    if (cachedBooks.isEmpty()) {
                        mutableState.value = LibraryUiState(loaded = true, error = it.message)
                    } else {
                        mutableState.value = mutableState.value.copy(loading = false, loaded = true, error = null)
                    }
                }
        }
    }

    /**
     * Reads which ebooks have no filter results yet.
     *
     * Only ebooks are examined. An audiobook's filter state already has its own presentation, and
     * scanning every audiobook's directory for a file that is never there would be work for nothing.
     */
    private fun refreshEbookFilterState(books: List<LibraryBook>) {
        viewModelScope.launch {
            val store = com.audiochoice.mobile.narration.NarrationStore(context.filesDir)
            val missing = books
                .filter {
                    com.audiochoice.mobile.library.LibraryShelves.shelfFor(it) ==
                        com.audiochoice.mobile.library.LibraryShelf.EBOOKS
                }
                .filter { book ->
                    val sha = book.fingerprint.sha256
                    // A stored scan means results exist. Continuing without them is a decision
                    // already taken, so it is not reported as something outstanding.
                    store.textScan(sha) == null &&
                        !localAudio.narrationFlags(sha).continuedWithoutFilterResults
                }
                .map { it.fingerprint.sha256.lowercase() }
                .toSet()
            mutableState.value = mutableState.value.copy(ebooksWithoutFilterResults = missing)
        }
    }

    /**
     * Sends up the synopsis for books whose Explore entry has none.
     *
     * Descriptions are read at import, so books added before that carry none on the server.
     * Re-running the library upsert would supply one, but it also overwrites the stored
     * title, which would revert a correction the listener had made, so this reports the
     * description on its own.
     *
     * Only files still on the device can be read, and only editions the Explore catalogue
     * is actually missing a description for are touched, so this costs nothing once every
     * book the listener owns has one.
     */
    private suspend fun reportMissingDescriptions(
        accessToken: String,
        books: List<LibraryBook>,
        catalog: List<ExploreCatalogBook>,
    ) {
        val describedCatalogIDs = catalog
            .filter { !it.description.isNullOrBlank() }
            .map { it.catalogID.lowercase() }
            .toSet()
        val reader = com.audiochoice.mobile.importing.Mp4TagReader(context.contentResolver)
        books.forEach { book ->
            if (book.fingerprint.sha256.take(24).lowercase() in describedCatalogIDs) return@forEach
            val uri = localAudio.find(book.fingerprint.sha256) ?: return@forEach
            val synopsis = runCatching { reader.read(uri).synopsis }.getOrNull() ?: return@forEach
            runCatching {
                api.reportEditionDescription(
                    accessToken,
                    com.audiochoice.mobile.data.EditionDescriptionReportRequest(
                        book.fingerprint,
                        synopsis,
                    ),
                )
            }
        }
    }

    private suspend fun enrichBook(
        accessToken: String,
        book: LibraryBook,
        catalog: List<ExploreCatalogBook>,
    ): LibraryBook {
        val match = catalog.firstOrNull { book.matchesCatalog(it) } ?: return book
        val fingerprint = book.fingerprint.copy(
            duration = match.duration ?: book.fingerprint.duration,
            workTitle = match.title,
            author = match.author,
            seriesTitle = match.seriesTitle,
            seriesNumber = match.seriesNumber,
            editionType = match.editionType,
        )
        if (
            book.title == match.title &&
            book.author == match.author &&
            book.fingerprint == fingerprint &&
            (book.coverImageURL != null || match.coverImageURL == null)
        ) return book
        return api.saveBook(
            accessToken,
            LibraryBookUpsertRequest(
                fingerprint = fingerprint,
                title = match.title,
                author = match.author,
                narrator = book.narrator,
                coverImageURL = book.coverImageURL ?: match.coverImageURL,
            ),
        )
    }

    private fun LibraryBook.matchesCatalog(item: ExploreCatalogBook): Boolean {
        if (fingerprint.sha256.startsWith(item.catalogID, ignoreCase = true)) return true
        val ironFlamePart2 = normalizedTitle(title).contains("iron flame") &&
            title.contains("part 2", ignoreCase = true) &&
            normalizedTitle(item.title).contains("iron flame") &&
            item.title.contains("part 2", ignoreCase = true)
        if (ironFlamePart2) return true
        val sameTitle = normalizedTitle(title) == normalizedTitle(item.title) ||
            normalizedTitle(fingerprint.workTitle.orEmpty()) == normalizedTitle(item.title)
        if (!sameTitle) return false
        val bookPart = Regex("part\\s*(\\d+)\\s*of\\s*(\\d+)", RegexOption.IGNORE_CASE)
            .find(title + " " + fingerprint.workTitle.orEmpty())?.groupValues?.drop(1)
        val catalogPart = Regex("part\\s*(\\d+)\\s*of\\s*(\\d+)", RegexOption.IGNORE_CASE)
            .find(item.title)?.groupValues?.drop(1)
        return bookPart == null || catalogPart == null || bookPart == catalogPart
    }

    private fun normalizedTitle(value: String): String = value.lowercase()
        .replace(Regex("\\(?(dramatized adaptation|graphic ?audio)\\)?"), "")
        .replace(Regex("\\bpart\\s+(?=\\d+\\s+of\\s+\\d+)"), "")
        .replace(Regex("[^a-z0-9]+"), " ")
        .trim()

    /**
     * One-time repair for the confirmed legacy Iron Flame Part 2 duplicate.
     * The raw record is identified by its exact canonical title and the old
     * "Imported audiobook" marker. Its local file link and any later progress
     * are moved to the real scanned edition before the stale account row is
     * removed. Scan results and the user's audio file are not touched.
     */
    /**
     * Older builds could create a plain "Imported audiobook" row before a
     * catalog edition was recognized. Keep the account history intact, but do
     * not show that incomplete row beside the real scanned edition.
     */
    private fun suppressLegacyImportDuplicates(books: List<LibraryBook>): List<LibraryBook> =
        books.filterNot { candidate ->
            candidate.author.equals("Imported audiobook", ignoreCase = true) &&
                books.any { canonical ->
                    canonical.id != candidate.id &&
                        !canonical.author.equals("Imported audiobook", ignoreCase = true) &&
                        isSameLegacyImport(candidate, canonical)
                }
        }

    private fun isSameLegacyImport(raw: LibraryBook, canonical: LibraryBook): Boolean {
        val rawTitle = raw.title.replace(Regex("([a-z])([A-Z])"), "$1 $2").lowercase()
        val canonicalTerms = canonical.title.lowercase()
            .replace(Regex("\\b(the|of|dramatized|adaptation)\\b"), " ")
            .split(Regex("[^a-z0-9]+"))
            .filter { it.length > 2 }
            .toSet()
        val rawTerms = rawTitle.split(Regex("[^a-z0-9]+"))
            .filter { it.length > 2 }
            .toSet()
        val rawPart = Regex("part\\s*(\\d+)").find(rawTitle)?.groupValues?.getOrNull(1)
        val canonicalPart = Regex("part\\s*(\\d+)", RegexOption.IGNORE_CASE)
            .find(canonical.title)?.groupValues?.getOrNull(1)
        return canonicalTerms.isNotEmpty() && canonicalTerms.all(rawTerms::contains) &&
            (rawPart == null || canonicalPart == null || rawPart == canonicalPart)
    }

    private fun compactTitle(value: String): String = value.lowercase().filter(Char::isLetterOrDigit)

    private fun hideConfirmedIronFlameCatalogDuplicate(
        books: List<ExploreCatalogBook>,
    ): List<ExploreCatalogBook> {
        // Iron Flame is intentionally not part of the public Explore catalog.
        // Keep it available in a user's Library, but never render it here even
        // if an older backend revision or cached response returns one entry.
        return books.filterNot { compactTitle(it.title).contains("ironflame") }
    }

    class Factory(
        context: android.content.Context,
        private val api: AudioChoiceApi,
        private val localAudio: com.audiochoice.mobile.data.LocalAudioStore,
    ) : ViewModelProvider.Factory {
        private val applicationContext = context.applicationContext
        @Suppress("UNCHECKED_CAST")
        override fun <T : ViewModel> create(modelClass: Class<T>): T =
            LibraryViewModel(applicationContext, api, localAudio) as T
    }
}
