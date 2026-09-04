package com.audiochoice.mobile.importing

import android.content.ContentResolver
import android.net.Uri
import android.content.Context
import android.os.PowerManager
import androidx.documentfile.provider.DocumentFile
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import com.audiochoice.contracts.CloudScanResponse
import com.audiochoice.contracts.CloudScanStatus
import kotlin.math.roundToInt
import com.audiochoice.contracts.EditionSignature
import com.audiochoice.mobile.data.AudioChoiceApi
import com.audiochoice.mobile.data.LibraryBook
import com.audiochoice.mobile.data.LibraryBookUpsertRequest
import com.audiochoice.mobile.data.ExploreCatalogBook
import com.audiochoice.mobile.data.LocalAudioStore
import com.audiochoice.mobile.data.ConversionConsentRequest
import com.audiochoice.mobile.beta.BetaConfig
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import java.time.Instant
import java.io.File
import java.io.FileOutputStream
import java.net.HttpURLConnection
import java.net.URL
import com.audiochoice.mobile.narration.NarrationConfig
import java.security.MessageDigest

enum class ImportPhase(val label: String) {
    IDLE("Choose an audiobook"), AGREEMENT("Ownership agreement"), CONVERTING("Converting locally"),
    CONVERSION_COMPLETE("Conversion complete"),
    READING("Reading audiobook"), FINGERPRINTING("Fingerprinting file"),
    SEARCHING("Searching scan library"), UPLOADING("Private upload"), ANALYZING("Analyzing content"),
    COMPLETE("Filter scan ready"), FAILED("Import failed")
}

data class ImportUiState(
    val phase: ImportPhase = ImportPhase.IDLE,
    val fileName: String? = null,
    val completedSteps: Int = 0,
    val result: CloudScanResponse? = null,
    val savedBook: LibraryBook? = null,
    val error: String? = null,
    val conversionProgress: Float = 0f,
    val statusMessage: String? = null,
    val scanProgress: Int = 0,
    val completedChunks: Int = 0,
    val totalChunks: Int = 0,
    val showBetaRestriction: Boolean = false,
    val showOrganizationPrompt: Boolean = false,
    /**
     * True when the file carried no usable tags, so the title is a tidied guess
     * from the filename rather than a known edition.
     */
    val titleFromFilename: Boolean = false,
    val organizingFile: Boolean = false,
    val organizationMessage: String? = null,
    val organizationComplete: Boolean = false,
    /**
     * Set only on the ebook path, and the marker the import screen branches on.
     *
     * Null for every audiobook import, which is what keeps the screen below unchanged for the
     * path that ships today.
     */
    val ebookOutcome: com.audiochoice.mobile.narration.NarrationImportOutcome? = null,
)

class ImportViewModel(
    private val api: AudioChoiceApi,
    private val inspector: AudioFileInspector,
    private val localAudio: LocalAudioStore,
    private val aaxConverter: AaxConverter,
    private val activeScanStore: ActiveScanStore,
    private val appFilesDirectory: File,
    private val appContext: Context,
) : ViewModel() {
    // Created with the app, before any audiobook is imported. This is private
    // app storage, so Android does not require a folder-selection permission.
    private val managedAudiobooksDirectory = File(appFilesDirectory, "AudioChoice/Audiobooks").apply { mkdirs() }
    private val mutableState = MutableStateFlow(ImportUiState())
    val state: StateFlow<ImportUiState> = mutableState.asStateFlow()

    private var pendingAaxUri: Uri? = null
    private var pendingConvertedUri: Uri? = null
    private var pendingImportUri: Uri? = null
    private var pendingAaxCoverBytes: ByteArray? = null
    private var pendingAaxBetaEdition: com.audiochoice.mobile.beta.BetaApprovedEdition? = null
    private var ownerTestingAccess = false
    private var resumingScan = false

    fun setOwnerTestingAccess(email: String) {
        ownerTestingAccess = BetaConfig.hasOwnerTestingAccess(email)
    }

    fun select(uri: Uri, resolver: ContentResolver, accessToken: String) {
        val fileName = queryFileName(uri, resolver)
        if (fileName.substringAfterLast('.', "").equals("aax", ignoreCase = true)) {
            pendingAaxUri = uri
            mutableState.value = ImportUiState(phase = ImportPhase.AGREEMENT, fileName = fileName)
        } else if (NarrationConfig.enabled &&
            com.audiochoice.mobile.narration.NarrationImportCoordinator.isEpub(fileName)
        ) {
            // Routed by file name, from the same picker and the same button. The listener does not
            // choose a kind of import; the file decides which shelf it lands on.
            //
            // Gated on the experimental build, so a beta build reaches the audiobook path below
            // exactly as it does today -- and its picker never offers an EPUB in the first place.
            importEbook(uri, resolver, accessToken, fileName)
        } else {
            import(uri, resolver, accessToken)
        }
    }

    /**
     * Imports an EPUB as a narrated book.
     *
     * Shares no step with the audiobook pipeline below beyond the phase labels: there is no
     * upload, no transcription and no scan job, because the book's text is read on this device.
     * The phases reused here are the ones that mean the same thing for both.
     */
    private fun importEbook(
        uri: Uri,
        resolver: ContentResolver,
        accessToken: String,
        fileName: String,
    ) {
        val coordinator = com.audiochoice.mobile.narration.NarrationImportCoordinator(
            api = api,
            localAudio = localAudio,
            filesDirectory = appFilesDirectory,
        )
        mutableState.value = ImportUiState(
            phase = ImportPhase.FINGERPRINTING,
            fileName = fileName,
            statusMessage = "Reading this ebook…",
        )
        viewModelScope.launch {
            val outcome = runCatching { coordinator.import(uri, resolver, accessToken) }
                .getOrElse { failure ->
                    com.audiochoice.mobile.narration.NarrationImportOutcome.Failed(
                        failure.message ?: "That ebook could not be imported.",
                    )
                }
            mutableState.value = when (outcome) {
                is com.audiochoice.mobile.narration.NarrationImportOutcome.Imported ->
                    ImportUiState(
                        phase = ImportPhase.COMPLETE,
                        fileName = fileName,
                        titleFromFilename = outcome.titleWasDerived,
                        ebookOutcome = outcome,
                        // Publishing the saved row is what makes the library reload. Without it
                        // the listener lands on a cached list with no ebook in it, and the Ebooks
                        // tab -- which only appears once there is one -- never shows up.
                        savedBook = outcome.libraryBook,
                        statusMessage = "“${outcome.title}” is in your Ebooks library.",
                    )

                is com.audiochoice.mobile.narration.NarrationImportOutcome.AlreadyInLibrary ->
                    ImportUiState(
                        phase = ImportPhase.COMPLETE,
                        fileName = fileName,
                        ebookOutcome = outcome,
                        // Deliberately not an error. Re-importing a book after moving the file is
                        // the ordinary way to reach this, and its rendered audio is intact.
                        statusMessage = "That ebook is already in your library.",
                    )

                is com.audiochoice.mobile.narration.NarrationImportOutcome.Declined ->
                    ImportUiState(
                        phase = ImportPhase.FAILED,
                        fileName = fileName,
                        ebookOutcome = outcome,
                        error = outcome.message.headline,
                        statusMessage = outcome.message.explanation,
                    )

                com.audiochoice.mobile.narration.NarrationImportOutcome.PermissionRefused ->
                    ImportUiState(
                        phase = ImportPhase.FAILED,
                        fileName = fileName,
                        ebookOutcome = outcome,
                        error = "That file could not be opened for reading.",
                    )

                is com.audiochoice.mobile.narration.NarrationImportOutcome.Failed ->
                    ImportUiState(
                        phase = ImportPhase.FAILED,
                        fileName = fileName,
                        ebookOutcome = outcome,
                        error = outcome.message,
                    )
            }
        }
    }

    /**
     * Starts the normal import pipeline for an audio URI delivered by another
     * Android app (including the future companion transfer). It intentionally
     * clears only a finished/failed import screen, never an active conversion or
     * cloud scan.
     */
    fun importTransferred(uri: Uri, resolver: ContentResolver, accessToken: String): Boolean {
        if (mutableState.value.phase !in listOf(ImportPhase.IDLE, ImportPhase.COMPLETE, ImportPhase.FAILED)) {
            return false
        }
        if (mutableState.value.phase != ImportPhase.IDLE) reset()
        select(uri, resolver, accessToken)
        return true
    }

    /** Claims an account-paired companion transfer, verifies every byte locally,
     * and then starts the normal import used for a file picked on device. */
    fun claimCompanionTransfer(uri: Uri, resolver: ContentResolver, accessToken: String): Boolean {
        val transferID = uri.pathSegments.lastOrNull()?.takeIf { it.isNotBlank() } ?: return false
        val code = uri.getQueryParameter("code")?.takeIf { it.isNotBlank() } ?: return false
        if (uri.scheme !in setOf("audiochoice", "audiochoice-beta") || uri.host != "transfer" ||
            mutableState.value.phase !in listOf(ImportPhase.IDLE, ImportPhase.COMPLETE, ImportPhase.FAILED)) return false
        if (mutableState.value.phase != ImportPhase.IDLE) reset()
        val wakeLock = (appContext.getSystemService(Context.POWER_SERVICE) as PowerManager)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "AudioChoice::CompanionTransfer")
            .apply { setReferenceCounted(false); acquire(6 * 60 * 60 * 1000L) }
        viewModelScope.launch {
            // try/finally rather than releasing on the success and failure paths:
            // viewModelScope is cancelled when the Activity finishes, and a
            // cancellation skipped the release entirely, pinning the CPU awake for
            // the full six-hour timeout.
            try {
                runCatching {
                    update(ImportPhase.READING, 0)
                    mutableState.value = mutableState.value.copy(statusMessage = "Receiving audiobook from your companion…")
                    val transfer = api.claimCompanionTransfer(accessToken, transferID, code)
                    val destination = downloadCompanionAudio(transfer.downloadURL, transfer.fileName, transfer.fileSize, transfer.sha256)
                    api.completeCompanionTransfer(accessToken, transfer.transferID)
                    // The handoff is now a verified local audio file. Reset the temporary
                    // receive progress before deliberately entering the normal import
                    // state machine, which only begins from an idle state.
                    mutableState.value = ImportUiState()
                    import(Uri.fromFile(destination), resolver, accessToken)
                }.onFailure { error ->
                    mutableState.value = mutableState.value.copy(
                        phase = ImportPhase.FAILED,
                        error = error.message ?: "AudioChoice could not receive that companion transfer.",
                    )
                }
            } finally {
                if (wakeLock.isHeld) wakeLock.release()
            }
        }
        return true
    }

    private suspend fun downloadCompanionAudio(url: String, fileName: String, expectedSize: Long, expectedSha256: String): File =
        kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
            val directory = File(appFilesDirectory, "incoming").apply { mkdirs() }
            val safeName = fileName.replace(Regex("[^A-Za-z0-9._ -]"), "_").ifBlank { "audiobook.m4b" }
            val destination = File(directory, "${System.currentTimeMillis()}-$safeName")
            val connection = (URL(url).openConnection() as HttpURLConnection).apply {
                connectTimeout = 30_000
                // M4A/M4B files can be large, and the phone may be locked while
                // the relay is downloading. Keep the socket alive long enough
                // for a slow or backgrounded transfer to finish.
                readTimeout = 30 * 60 * 1000
            }
            try {
                val digest = MessageDigest.getInstance("SHA-256")
                var copied = 0L
                connection.inputStream.use { input ->
                    FileOutputStream(destination).use { output ->
                        val buffer = ByteArray(1024 * 1024)
                        while (true) {
                            val count = input.read(buffer)
                            if (count < 0) break
                            output.write(buffer, 0, count)
                            digest.update(buffer, 0, count)
                            copied += count
                            mutableState.value = mutableState.value.copy(
                                scanProgress = ((copied * 100) / expectedSize.coerceAtLeast(1)).toInt().coerceIn(0, 100),
                                statusMessage = "Receiving audiobook — ${((copied * 100) / expectedSize.coerceAtLeast(1)).coerceIn(0, 100)}%",
                            )
                        }
                    }
                }
                val hash = digest.digest().joinToString("") { "%02X".format(it) }
                require(copied == expectedSize && hash.equals(expectedSha256, ignoreCase = true)) {
                    destination.delete()
                    "The received audiobook could not be verified."
                }
                destination
            } catch (error: Throwable) {
                destination.delete()
                throw error
            } finally { connection.disconnect() }
        }

    fun acceptAaxAgreement(resolver: ContentResolver, accessToken: String) {
        val uri = pendingAaxUri ?: return
        val fileName = mutableState.value.fileName ?: "Imported audiobook.aax"
        viewModelScope.launch {
            runCatching {
                update(ImportPhase.CONVERTING, 0)
                val inspected = inspector.inspect(uri)
                pendingAaxCoverBytes = inspected.coverBytes
                // Bind an approved AAX to its catalog edition before remuxing.
                // The local M4B may have different bytes and fewer tags.
                pendingAaxBetaEdition = if (BetaConfig.enabled && !ownerTestingAccess) {
                    BetaConfig.approvedAaxSourceEdition(inspected.fingerprint, api.explore(accessToken))
                } else null
                // AAX conversion is deliberately device-local. A temporary API/network
                // problem must never prevent an owner from converting their own file.
                // Keep the server audit record as best-effort metadata instead of making
                // it a prerequisite for the local converter.
                runCatching {
                    api.recordConversionConsent(accessToken, ConversionConsentRequest(
                        fingerprint = inspected.fingerprint,
                        sourceFileName = fileName,
                        agreementVersion = LocalAaxConverter.AGREEMENT_VERSION,
                        agreementText = LocalAaxConverter.AGREEMENT_TEXT,
                    ))
                }
                when (val conversion = aaxConverter.convert(
                    uri,
                    fileName,
                    AaxOwnershipAcceptance(LocalAaxConverter.AGREEMENT_VERSION, Instant.now(), fileName),
                ) { progress -> mutableState.value = mutableState.value.copy(conversionProgress = progress) }) {
                    is AaxConversionResult.Converted -> {
                        pendingAaxCoverBytes = conversion.coverBytes ?: pendingAaxCoverBytes
                        pendingConvertedUri = conversion.uri
                        mutableState.value = mutableState.value.copy(
                            phase = ImportPhase.CONVERSION_COMPLETE,
                            fileName = conversion.fileName,
                            conversionProgress = 1f,
                            error = null,
                        )
                    }
                    is AaxConversionResult.AuthorizationRequired -> mutableState.value = mutableState.value.copy(
                        phase = ImportPhase.FAILED,
                        error = conversion.message,
                    )
                }
            }.onFailure {
                mutableState.value = mutableState.value.copy(
                    phase = ImportPhase.FAILED,
                    error = it.message ?: "The ownership acknowledgment could not be stored.",
                )
            }
        }
    }

    fun declineAaxAgreement() {
        pendingAaxUri = null
        reset()
    }

    fun scanConverted(resolver: ContentResolver, accessToken: String) {
        val converted = pendingConvertedUri ?: return
        import(converted, resolver, accessToken, pendingAaxBetaEdition)
    }

    /**
     * Scans a book already in the library again.
     *
     * A book can end up with no filter data through nothing the listener did: an edition nobody has
     * scanned has nothing to inherit, and a scan interrupted before the server created a job leaves
     * nothing for recovery to resume. Without this the warning in the player was a dead end -- it said
     * filters were inactive and offered no way to change that.
     *
     * Runs the ordinary import path. The library row is an upsert keyed by the file's fingerprint, so
     * repeating it re-scans the same book rather than creating a second one, and every step -- upload,
     * polling, notification, the active-scan record that survives the app being killed -- behaves
     * exactly as it does the first time instead of being reimplemented here.
     */
    fun rescan(uri: Uri, resolver: ContentResolver, accessToken: String) {
        import(uri, resolver, accessToken)
    }

    fun resumeActiveScan(resolver: ContentResolver, accessToken: String) {
        if (BetaConfig.enabled && !ownerTestingAccess) {
            activeScanStore.clear()
            return
        }
        if (resumingScan || mutableState.value.phase != ImportPhase.IDLE) return
        val activeScan = activeScanStore.load() ?: return
        resumingScan = true
        pendingImportUri = activeScan.audioUri
        mutableState.value = mutableState.value.copy(
            phase = ImportPhase.ANALYZING,
            fileName = activeScan.fileName,
            completedSteps = 4,
            statusMessage = "Reconnecting to your private scan…",
        )
        viewModelScope.launch {
            runCatching {
                val inspected = inspector.inspect(activeScan.audioUri)
                val audio = inspected.copy(coverBytes = inspected.coverBytes ?: pendingAaxCoverBytes)
                val result = poll(accessToken, activeScan.scanID)
                val book = saveMatchedBook(accessToken, audio)
                // A completed scan can resolve to an existing library edition whose
                // canonical fingerprint differs from the locally imported file. Keep
                // both keys pointing at the same on-device audio so reopening the
                // library record never incorrectly asks the listener to reimport.
                localAudio.save(audio.fingerprint.sha256, activeScan.audioUri, audio.chapters, audio.coverBytes)
                if (!book.fingerprint.sha256.equals(audio.fingerprint.sha256, ignoreCase = true)) {
                    localAudio.save(book.fingerprint.sha256, activeScan.audioUri, audio.chapters, audio.coverBytes)
                }
                activeScanStore.complete(activeScan.fileName)
                mutableState.value = mutableState.value.copy(
                    phase = ImportPhase.COMPLETE,
                    completedSteps = 6,
                    result = result,
                    savedBook = book,
                    statusMessage = null,
                )
            }.onFailure {
                mutableState.value = mutableState.value.copy(
                    phase = ImportPhase.FAILED,
                    error = it.message ?: "AudioChoice could not reconnect to that scan.",
                )
            }
            resumingScan = false
        }
    }

    private fun import(
        uri: Uri,
        resolver: ContentResolver,
        accessToken: String,
        approvedAaxEdition: com.audiochoice.mobile.beta.BetaApprovedEdition? = null,
    ) {
        if (mutableState.value.phase !in listOf(
                ImportPhase.IDLE,
                ImportPhase.CONVERTING,
                ImportPhase.CONVERSION_COMPLETE,
                ImportPhase.COMPLETE,
                ImportPhase.FAILED,
            )
        ) return
        pendingImportUri = uri
        val wakeLock = (appContext.getSystemService(Context.POWER_SERVICE) as PowerManager)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "AudioChoice::PrivateImport")
            .apply { setReferenceCounted(false); acquire(6 * 60 * 60 * 1000L) }
        viewModelScope.launch {
            // try/finally is required here: releasing on the success and failure
            // paths alone missed both coroutine cancellation and
            // rejectUnsupportedBetaImport(), which returns through runCatching's
            // success path before reaching the release call.
            try {
            runCatching {
                update(ImportPhase.READING, 0)
                val inspected = inspector.inspect(uri)
                val audio = inspected.copy(coverBytes = inspected.coverBytes ?: pendingAaxCoverBytes)
                mutableState.value = mutableState.value.copy(fileName = audio.fileName)
                update(ImportPhase.FINGERPRINTING, 1)
                // During this beta pass, allow testers to import any audiobook so we can
                // validate fingerprinting and catalog discovery beyond the initial allowlist.
                // The file still follows the normal authenticated private-scan flow.
                val restrictedBeta = false
                // The beta catalog request can take a moment. Move the UI to the
                // lookup phase before making it so a slow network cannot look like
                // a fingerprinting hang.
                update(ImportPhase.SEARCHING, 2)
                val catalogEdition = if (BetaConfig.enabled) {
                    val catalog = api.explore(accessToken)
                    approvedAaxEdition ?: BetaConfig.approvedEdition(audio.fingerprint, catalog)
                } else null
                if (restrictedBeta && catalogEdition == null) {
                    rejectUnsupportedBetaImport()
                    return@runCatching
                }
                // Read now, before the lookup below, rather than reported only afterward as
                // part of saveMatchedBook's library upsert: the whole point of sending it is
                // to let THIS lookup recognise a converted or re-tagged copy of an edition
                // already scanned, which it cannot do with evidence that only arrives later.
                val importSignature = EditionSignatures.from(audio.tags, audio.chapters)
                // Always reuse a matching shared scan first. This is especially
                // important for locally converted/retagged files whose byte hash
                // differs from the file originally scanned. The owner may still
                // start a new Lambda scan when no existing edition matches.
                val existing = catalogEdition?.let { edition ->
                    api.exploreFilterResult(accessToken, edition.catalogBook.catalogID)
                } ?: api.findScan(accessToken, audio.fingerprint, importSignature)
                val result = if (catalogEdition != null) {
                    require(
                        existing.status in setOf(CloudScanStatus.AVAILABLE, CloudScanStatus.COMPLETED) &&
                            existing.result != null
                    ) { "The saved beta filter data for this edition is temporarily unavailable." }
                    existing
                } else when (existing.status) {
                    CloudScanStatus.AVAILABLE, CloudScanStatus.COMPLETED -> existing
                    CloudScanStatus.QUEUED, CloudScanStatus.PROCESSING -> poll(accessToken, requireNotNull(existing.scanID))
                    CloudScanStatus.UPLOAD_REQUIRED -> {
                        update(ImportPhase.UPLOADING, 3)
                        val authorization = api.authorizeUpload(accessToken, audio.fingerprint, audio.fileName, audio.contentType)
                        api.uploadAudio(authorization, resolver, uri, audio.fingerprint.fileSize) { progress ->
                            val percentage = (progress * 100).toInt().coerceIn(0, 100)
                            mutableState.value = mutableState.value.copy(
                                scanProgress = percentage,
                                statusMessage = "Uploading privately — $percentage%",
                            )
                        }
                        api.completeUpload(accessToken, authorization.uploadID)
                        update(ImportPhase.ANALYZING, 4)
                        val submitted = api.submitScan(accessToken, authorization.uploadID, audio.fingerprint)
                        if (submitted.result != null) submitted else poll(accessToken, requireNotNull(submitted.scanID))
                    }
                    CloudScanStatus.FAILED -> error("An earlier scan failed. Please try importing the audiobook again.")
                }
                val book = saveMatchedBook(
                    accessToken,
                    audio,
                    catalogEdition?.catalogBook,
                    catalogEdition?.part,
                )
                // Store the file under its source fingerprint and, when the backend
                // matched it to a canonical scanned edition, under that edition's
                // fingerprint too. This supports M4B/M4A/AAX conversions without
                // losing the local playback link or triggering a needless reimport.
                val stableUri = preserveForPlayback(uri, resolver, audio.fileName, audio.fingerprint.sha256)
                localAudio.save(audio.fingerprint.sha256, stableUri, audio.chapters, audio.coverBytes)
                if (!book.fingerprint.sha256.equals(audio.fingerprint.sha256, ignoreCase = true)) {
                    localAudio.save(book.fingerprint.sha256, stableUri, audio.chapters, audio.coverBytes)
                }
                activeScanStore.complete(audio.fileName)
                // The playback copy is registered by now, so any leftover
                // download or conversion intermediate for this import is
                // unreferenced and safe to reclaim.
                localAudio.purgeOrphanedAudioFiles()
                pendingAaxCoverBytes = null
                pendingAaxBetaEdition = null
                mutableState.value = mutableState.value.copy(
                    phase = ImportPhase.COMPLETE, completedSteps = 6, result = result, savedBook = book,
                    showOrganizationPrompt = BetaConfig.enabled,
                    // A catalog match supplies a real edition title; otherwise the
                    // title is only as good as the file's own tags.
                    titleFromFilename = catalogEdition == null && !audio.isTitleFromMetadata,
                )
            }.onFailure {
                mutableState.value = mutableState.value.copy(
                    phase = ImportPhase.FAILED, error = it.message ?: "AudioChoice could not import that audiobook.",
                )
            }
            } finally {
                if (wakeLock.isHeld) wakeLock.release()
            }
        }
    }

    fun retry(resolver: ContentResolver, accessToken: String) {
        val uri = pendingImportUri ?: pendingConvertedUri ?: pendingAaxUri ?: return
        import(uri, resolver, accessToken)
    }

    fun reset() {
        pendingAaxUri = null
        pendingConvertedUri = null
        pendingImportUri = null
        pendingAaxCoverBytes = null
        pendingAaxBetaEdition = null
        activeScanStore.clear()
        mutableState.value = ImportUiState()
    }

    /**
     * Clears a finished import when its screen is left, while deliberately preserving any
     * conversion or cloud scan that is still in progress.
     */
    fun onImportScreenLeft() {
        if (mutableState.value.phase == ImportPhase.COMPLETE) reset()
    }

    fun dismissBetaRestriction() {
        mutableState.value = mutableState.value.copy(showBetaRestriction = false)
    }

    fun leaveFileInPlace() {
        mutableState.value = mutableState.value.copy(showOrganizationPrompt = false)
    }

    /** Stores a verified playable copy in AudioChoice's automatically created folder. */
    fun organizeInAudioChoiceStorage(resolver: ContentResolver) {
        if (!BetaConfig.enabled || mutableState.value.organizingFile) return
        val source = pendingImportUri ?: pendingConvertedUri ?: return
        viewModelScope.launch {
            mutableState.value = mutableState.value.copy(
                showOrganizationPrompt = false,
                organizingFile = true,
                organizationMessage = "Moving audiobook to AudioChoice…",
            )
            runCatching {
                require(managedAudiobooksDirectory.exists() || managedAudiobooksDirectory.mkdirs()) {
                    "AudioChoice storage could not be created."
                }
                val stableID = mutableState.value.savedBook?.fingerprint?.sha256
                    ?.lowercase()?.take(16) ?: "audiobook"
                val title = mutableState.value.savedBook?.title.orEmpty()
                    .replace(Regex("[^A-Za-z0-9._ -]"), "_").trim().take(80)
                    .ifBlank { "Audiobook" }
                val bookFolder = File(managedAudiobooksDirectory, "$title [$stableID]").apply { mkdirs() }
                require(bookFolder.isDirectory) { "AudioChoice could not create the audiobook folder." }
                val sourceName = mutableState.value.fileName ?: "audiobook.m4b"
                val targetFile = File(bookFolder, sourceName)
                resolver.openInputStream(source).use { input ->
                    requireNotNull(input) { "The imported audiobook could not be reopened." }
                    FileOutputStream(targetFile, false).use { output -> input.copyTo(output, 1024 * 1024) }
                }
                val expected = resolver.openAssetFileDescriptor(source, "r")?.use { it.length }
                require(expected == null || expected < 0 || expected == targetFile.length()) {
                    targetFile.delete()
                    "The copied audiobook did not pass verification. The original was left unchanged."
                }
                val targetUri = Uri.fromFile(targetFile)
                val sha = requireNotNull(mutableState.value.savedBook?.fingerprint?.sha256)
                localAudio.save(sha, targetUri, localAudio.chapters(sha), pendingAaxCoverBytes)
                // Companion and local conversion sources belong to AudioChoice and can be
                // removed after verification. Android may protect other app-owned files.
                if (source.scheme == "file" && source.path?.startsWith(appFilesDirectory.path) == true) {
                    File(requireNotNull(source.path)).delete()
                } else if (source.scheme == "content") {
                    runCatching { resolver.delete(source, null, null) }
                }
                targetUri
            }.onSuccess {
                pendingImportUri = it
                pendingConvertedUri = it
                mutableState.value = mutableState.value.copy(
                    organizingFile = false,
                    organizationMessage = "Audiobook organized in AudioChoice/Audiobooks.",
                    organizationComplete = true,
                )
            }.onFailure {
                mutableState.value = mutableState.value.copy(
                    organizingFile = false,
                    organizationMessage = it.message ?: "The audiobook could not be organized. The original is unchanged.",
                )
            }
        }
    }

    /**
     * Copies the imported audiobook only after the listener explicitly chooses
     * a location. Managed storage creates the AudioChoice folder tree; manual
     * storage places the file directly in the folder the listener selected.
     */
    fun organizeInSelectedFolder(context: Context, rootUri: Uri, audioChoiceManaged: Boolean) {
        if (!BetaConfig.enabled || mutableState.value.organizingFile) return
        val source = pendingImportUri ?: pendingConvertedUri ?: return
        viewModelScope.launch {
            mutableState.value = mutableState.value.copy(
                showOrganizationPrompt = false,
                organizingFile = true,
                organizationMessage = "Organizing your audiobook…",
            )
            runCatching {
                val resolver = context.contentResolver
                resolver.takePersistableUriPermission(
                    rootUri,
                    android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION or
                        android.content.Intent.FLAG_GRANT_WRITE_URI_PERMISSION,
                )
                val selected = requireNotNull(DocumentFile.fromTreeUri(context, rootUri)) {
                    "That folder could not be opened."
                }
                require(selected.canWrite()) { "AudioChoice needs write access to that folder." }
                val stableID = mutableState.value.savedBook?.fingerprint?.sha256
                    ?.lowercase()?.take(16) ?: "audiobook"
                val title = mutableState.value.savedBook?.title.orEmpty()
                    .replace(Regex("[^A-Za-z0-9._ -]"), "_").trim().take(80)
                    .ifBlank { "Audiobook" }
                val destinationFolder = if (audioChoiceManaged) {
                    val audioChoice = if (selected.name.equals("AudioChoice", ignoreCase = true)) selected
                        else selected.findFile("AudioChoice") ?: selected.createDirectory("AudioChoice")
                    val audiobooks = requireNotNull(
                        audioChoice?.findFile("Audiobooks") ?: audioChoice?.createDirectory("Audiobooks"),
                    ) { "The AudioChoice/Audiobooks folder could not be created." }
                    requireNotNull(
                        audiobooks.findFile("$title [$stableID]")
                            ?: audiobooks.createDirectory("$title [$stableID]"),
                    ) { "The audiobook folder could not be created." }
                } else {
                    selected
                }
                val sourceName = mutableState.value.fileName ?: "audiobook.m4b"
                destinationFolder.findFile(sourceName)?.delete()
                val target = requireNotNull(destinationFolder.createFile(
                    resolver.getType(source) ?: "audio/mp4",
                    sourceName,
                )) { "The managed audiobook file could not be created." }
                resolver.openInputStream(source).use { input ->
                    requireNotNull(input) { "The imported audiobook could not be reopened." }
                    resolver.openOutputStream(target.uri, "w").use { output ->
                        requireNotNull(output) { "The managed audiobook file could not be written." }
                        input.copyTo(output, 1024 * 1024)
                    }
                }
                val expected = resolver.openAssetFileDescriptor(source, "r")?.use { it.length }
                val actual = resolver.openAssetFileDescriptor(target.uri, "r")?.use { it.length }
                require(expected == null || expected < 0 || expected == actual) {
                    "The copied audiobook did not pass verification. The original was left unchanged."
                }
                val sha = requireNotNull(mutableState.value.savedBook?.fingerprint?.sha256)
                localAudio.save(sha, target.uri, localAudio.chapters(sha), pendingAaxCoverBytes)
                // "Yes, choose folder" is the user's explicit move authorization. Delete only after
                // the managed copy has passed the byte-length verification above.
                if (source.scheme == "file" && source.path?.startsWith(context.filesDir.path) == true) {
                    java.io.File(requireNotNull(source.path)).delete()
                } else if (source.scheme == "content") {
                    resolver.delete(source, null, null)
                }
                target.uri
            }.onSuccess {
                pendingImportUri = it
                pendingConvertedUri = it
                mutableState.value = mutableState.value.copy(
                    organizingFile = false,
                    organizationMessage = if (audioChoiceManaged) {
                        "Audiobook organized in AudioChoice/Audiobooks."
                    } else {
                        "Audiobook saved in the folder you selected."
                    },
                    organizationComplete = true,
                )
            }.onFailure {
                mutableState.value = mutableState.value.copy(
                    organizingFile = false,
                    organizationMessage = it.message ?: "The audiobook could not be organized. The original is unchanged.",
                )
            }
        }
    }

    private fun rejectUnsupportedBetaImport() {
        pendingAaxUri = null
        pendingConvertedUri = null
        pendingImportUri = null
        pendingAaxCoverBytes = null
        activeScanStore.clear()
        mutableState.value = ImportUiState(showBetaRestriction = true)
    }

    private fun queryFileName(uri: Uri, resolver: ContentResolver): String {
        resolver.query(uri, arrayOf(android.provider.OpenableColumns.DISPLAY_NAME), null, null, null)?.use {
            if (it.moveToFirst()) return it.getString(0) ?: "Imported audiobook"
        }
        // Companion transfers are downloaded into app-private storage and arrive as
        // file:// URIs. ContentResolver metadata is unavailable for those URIs, but
        // the path still contains the original sanitized filename.
        if (uri.scheme.equals("file", ignoreCase = true)) {
            return java.io.File(uri.path ?: "").name.takeIf { it.isNotBlank() }
                ?: "Imported audiobook"
        }
        return uri.lastPathSegment ?: "Imported audiobook"
    }

    private suspend fun poll(accessToken: String, scanID: String): CloudScanResponse {
        update(ImportPhase.ANALYZING, 4)
        pendingImportUri?.let { uri ->
            activeScanStore.save(scanID, uri, mutableState.value.fileName ?: "Imported audiobook")
        }
        while (true) {
            val response = try {
                api.scanJob(accessToken, scanID)
            } catch (_: Exception) {
                mutableState.value = mutableState.value.copy(
                    statusMessage = "The cloud scan is still running. AudioChoice will reconnect automatically.",
                )
                delay(15_000)
                continue
            }
            val stageLabel = response.progressStage?.replace('_', ' ')
                ?.replaceFirstChar { it.uppercase() }
                ?: "Transcribing and analyzing"
            val chunkLabel = if (response.totalChunks > 0)
                " — ${response.completedChunks.coerceAtLeast(0)}/${response.totalChunks} chunks"
            else ""
            mutableState.value = mutableState.value.copy(
                statusMessage = "$stageLabel$chunkLabel. This may take a while.",
                scanProgress = response.progressPercent.coerceIn(0, 100),
                completedChunks = response.completedChunks.coerceAtLeast(0),
                totalChunks = response.totalChunks.coerceAtLeast(0),
            )
            if (response.status == CloudScanStatus.COMPLETED) return response
            if (response.status == CloudScanStatus.FAILED) error(
                "The cloud scan could not be completed. Your local audiobook was not removed.",
            )
            delay(10_000)
        }
    }

    private suspend fun saveMatchedBook(
        accessToken: String,
        audio: InspectedAudio,
        knownCatalogBook: ExploreCatalogBook? = null,
        betaPart: Int? = null,
    ): LibraryBook {
        val accountBooks = runCatching { api.library(accessToken) }.getOrNull().orEmpty()
        val exactAccountBook = accountBooks.firstOrNull {
            it.fingerprint.sha256.equals(audio.fingerprint.sha256, ignoreCase = true)
        }
        val catalogBooks = runCatching { api.explore(accessToken) }.getOrNull().orEmpty()
        val catalogBook = knownCatalogBook
            ?: catalogBooks.firstOrNull { it.matches(audio.fingerprint.sha256) }
            ?: catalogBooks.firstOrNull {
                val audioTitle = normalizeEditionTitle(audio.title)
                val catalogTitle = normalizeEditionTitle(it.title)
                val audioAuthor = audio.fingerprint.author
                val authorMatches = audioAuthor.isNullOrBlank() || it.author.isNullOrBlank() ||
                    normalizeEditionTitle(audioAuthor) == normalizeEditionTitle(it.author.orEmpty())
                authorMatches && (audioTitle == catalogTitle ||
                    (audioTitle.contains("ironflame") && audioTitle.contains("part2") &&
                        catalogTitle.contains("ironflame") && catalogTitle.contains("part2")))
            }
        // A locally converted AAX/M4A/M4B will have a new byte hash even though
        // it is the same edition. When that edition is already in the listener's
        // Library, keep its canonical record and attach this local file to it
        // rather than making a second row.
        val existingEdition = exactAccountBook ?: catalogBook?.let { catalog ->
            accountBooks.singleOrNull { book -> book.matchesCatalogEdition(catalog) }
        }
        val fingerprint = existingEdition?.fingerprint ?: catalogBook?.let { catalog ->
            audio.fingerprint.copy(
                duration = catalog.duration ?: audio.fingerprint.duration,
                workTitle = catalog.title,
                author = catalog.author,
                seriesTitle = catalog.seriesTitle,
                seriesNumber = catalog.seriesNumber,
                editionType = catalog.editionType,
                partNumber = betaPart ?: audio.fingerprint.partNumber,
                totalParts = if (betaPart != null) 2 else audio.fingerprint.totalParts,
            )
        } ?: audio.fingerprint
        // The server only ever sees decoded audio, never the container tags, so this
        // evidence and the source-file link both have to come from here.
        val sourceFingerprint = EditionSignatures.sourceFingerprintFor(audio.fingerprint, fingerprint)
        val editionSignature = EditionSignatures.from(audio.tags, audio.chapters)
        val catalogID = catalogBook?.catalogID ?: audio.fingerprint.sha256.take(24)
        var coverImageURL = catalogBook?.coverImageURL
        if (coverImageURL == null && audio.coverBytes != null) {
            val uploaded = runCatching {
                api.uploadExploreCover(accessToken, catalogID, audio.coverBytes)
            }.getOrDefault(false)
            if (uploaded) coverImageURL = "/v1/explore/$catalogID/cover"
        }
        val savedBook = api.saveBook(
            accessToken,
            LibraryBookUpsertRequest(
                fingerprint = fingerprint,
                title = exactAccountBook?.title ?: catalogBook?.title ?: audio.title,
                author = exactAccountBook?.author ?: catalogBook?.author ?: fingerprint.author,
                // The request and both server stores have always carried a
                // narrator; nothing ever populated it. The container tags do.
                narrator = exactAccountBook?.narrator ?: audio.tags.narrator,
                coverImageURL = coverImageURL,
                coverImageBase64 = audio.coverBytes?.let { android.util.Base64.encodeToString(it, android.util.Base64.NO_WRAP) },
                coverImageContentType = audio.coverBytes?.let(::coverContentType),
                sourceFingerprint = sourceFingerprint,
                signature = editionSignature,
                // The file's own synopsis, which is what Explore shows for this edition.
                description = audio.tags.synopsis,
            ),
        )
        // Persist the embedded artwork against the canonical full fingerprint.
        // The local copy was already saved above for offline playback.
        if (audio.coverBytes != null) {
            runCatching {
                api.uploadEmbeddedCover(accessToken, savedBook.fingerprint, audio.coverBytes)
            }
        }
        return savedBook
    }

    /** Keeps playback independent of temporary permissions granted by file pickers
     * and cloud-drive providers. Companion files already live in private storage. */
    private suspend fun preserveForPlayback(
        source: Uri,
        resolver: ContentResolver,
        fileName: String,
        sha256: String,
    ): Uri = kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
        if (source.scheme == "file" && source.path?.startsWith(appFilesDirectory.path) == true) {
            return@withContext source
        }
        val extension = fileName.substringAfterLast('.', "audio")
            .replace(Regex("[^A-Za-z0-9]"), "")
            .ifBlank { "audio" }
        val directory = File(appFilesDirectory, "playback_audio").apply { mkdirs() }
        val destination = File(directory, "${sha256.lowercase()}.$extension")
        val temporary = File(directory, "${sha256.lowercase()}.partial")
        // Without this the partial copy survived every failure path, leaving a
        // full-size orphan behind for each interrupted import.
        try {
            resolver.openInputStream(source).use { input ->
                requireNotNull(input) { "The imported audiobook could not be reopened for playback." }
                FileOutputStream(temporary, false).use { output -> input.copyTo(output, 1024 * 1024) }
            }
            require(temporary.length() > 0) { "The playback copy of this audiobook was empty." }
            if (destination.exists()) destination.delete()
            require(temporary.renameTo(destination)) { "AudioChoice could not finish saving the playback copy." }
        } finally {
            if (temporary.exists()) temporary.delete()
        }
        Uri.fromFile(destination)
    }

    private fun ExploreCatalogBook.matches(sha256: String): Boolean =
        catalogID.equals(sha256.take(24), ignoreCase = true)

    private fun coverContentType(bytes: ByteArray): String = when {
        bytes.size >= 8 && bytes.copyOfRange(0, 8).contentEquals(
            byteArrayOf(0x89.toByte(), 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A),
        ) -> "image/png"
        bytes.size >= 12 && String(bytes, 0, 4) == "RIFF" && String(bytes, 8, 4) == "WEBP" -> "image/webp"
        else -> "image/jpeg"
    }

    private fun LibraryBook.matchesCatalogEdition(catalog: ExploreCatalogBook): Boolean =
        normalizeEditionTitle(title) == normalizeEditionTitle(catalog.title) &&
            normalizeEditionTitle(author.orEmpty()) == normalizeEditionTitle(catalog.author.orEmpty())

    private fun normalizeEditionTitle(value: String): String = value.lowercase()
        .filter(Char::isLetterOrDigit)

    private fun update(phase: ImportPhase, completed: Int) {
        mutableState.value = mutableState.value.copy(
            phase = phase,
            completedSteps = completed,
            error = null,
            // Upload progress reaches 100% before the server begins transcription. Do not
            // carry that completed local-stage value into the cloud-stage progress indicator.
            scanProgress = if (phase == ImportPhase.ANALYZING) 0 else mutableState.value.scanProgress,
            // Keep server-reported chunk progress when entering the analyzing phase.
            // Resetting it here made the UI show only “Preparing chunks…” until the
            // next poll and hid progress already persisted by the backend.
            completedChunks = mutableState.value.completedChunks,
            totalChunks = mutableState.value.totalChunks,
            statusMessage = if (phase == ImportPhase.ANALYZING)
                "Your audiobook is being transcribed and analyzed. This may take a while."
            else null,
        )
    }

    class Factory(
        private val api: AudioChoiceApi,
        private val inspector: AudioFileInspector,
        private val localAudio: LocalAudioStore,
        private val aaxConverter: AaxConverter,
        private val activeScanStore: ActiveScanStore,
        private val appFilesDirectory: File,
        private val appContext: Context,
    ) : ViewModelProvider.Factory {
        @Suppress("UNCHECKED_CAST")
        override fun <T : ViewModel> create(modelClass: Class<T>): T =
            ImportViewModel(api, inspector, localAudio, aaxConverter, activeScanStore, appFilesDirectory, appContext) as T
    }
}
