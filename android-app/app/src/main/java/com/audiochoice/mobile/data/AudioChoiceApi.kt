package com.audiochoice.mobile.data

import com.audiochoice.mobile.BuildConfig
import android.content.ContentResolver
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.util.Base64
import com.audiochoice.contracts.*
import java.io.ByteArrayOutputStream
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder
import java.io.IOException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.withContext
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json

class ApiException(val statusCode: Int, message: String) : Exception(message)

class AudioChoiceApi(private val json: Json) {
    private val baseUrl = BuildConfig.API_BASE_URL.trim().trimEnd('/')

    private val mutableSessionExpired = MutableSharedFlow<Unit>(
        extraBufferCapacity = 1,
        onBufferOverflow = BufferOverflow.DROP_OLDEST,
    )

    /**
     * Emits when an authenticated request is rejected with 401, meaning the
     * stored session is no longer usable.
     *
     * Previously every 401 was reported as "The email or password was not
     * accepted." regardless of endpoint, so an expired token surfaced as a
     * password error on the Library screen while the app stayed nominally
     * signed in and silently failed every subsequent call.
     */
    val sessionExpired: SharedFlow<Unit> = mutableSessionExpired.asSharedFlow()

    /** Sign-in and sign-out endpoints, where a 401 is a credential problem. */
    private fun isAuthenticationPath(path: String): Boolean = path.startsWith("/v1/auth/")

    suspend fun register(request: RegisterRequest): AuthResponse = post("/v1/auth/register", request)
    suspend fun login(request: LoginRequest): AuthResponse = post("/v1/auth/login", request)

    /**
     * Asks for a reset code to be emailed.
     *
     * Accepted whether or not the address has an account, deliberately: answering differently would
     * tell anyone who asks which addresses are registered here.
     */
    suspend fun requestPasswordReset(email: String): AuthActionResponse =
        post("/v1/auth/password-reset/request", PasswordResetRequest(email))

    suspend fun confirmPasswordReset(code: String, newPassword: String): AuthActionResponse =
        post("/v1/auth/password-reset/confirm", PasswordResetConfirmRequest(code, newPassword))
    suspend fun googleSignIn(identityToken: String): AuthResponse =
        post("/v1/auth/external", ExternalLoginRequest("google", identityToken = identityToken))

    suspend fun logout(accessToken: String) {
        request<Unit>("POST", "/v1/auth/logout", null, accessToken)
    }

    suspend fun sendSupportMessage(
        accessToken: String,
        supportRequest: SupportMessageRequest,
    ): SupportMessageResponse = post("/v1/support/messages", supportRequest, accessToken)

    suspend fun recordConversionConsent(
        accessToken: String,
        request: ConversionConsentRequest,
    ): ConversionConsentRecord = post("/v1/conversion-consents", request, accessToken)

    suspend fun library(accessToken: String): List<LibraryBook> = request("GET", "/v1/library", null, accessToken)
    suspend fun explore(accessToken: String): List<ExploreCatalogBook> = request("GET", "/v1/explore", null, accessToken)

    suspend fun uploadExploreCover(accessToken: String, catalogID: String, bytes: ByteArray): Boolean = withContext(Dispatchers.IO) {
        val uploadBytes = prepareCoverForUpload(bytes)
        val connection = (URL("$baseUrl/v1/explore/$catalogID/cover").openConnection() as HttpURLConnection).apply {
            requestMethod = "PUT"
            connectTimeout = 30_000
            readTimeout = 30_000
            doOutput = true
            setFixedLengthStreamingMode(uploadBytes.size)
            setRequestProperty("Authorization", "Bearer $accessToken")
            setRequestProperty("Content-Type", detectImageContentType(uploadBytes))
        }
        try {
            connection.outputStream.use { it.write(uploadBytes) }
            val status = connection.responseCode
            if (status == 404) return@withContext false
            if (status !in 200..299) throw ApiException(status, "Cover artwork could not be saved ($status).")
            true
        } finally {
            connection.disconnect()
        }
    }

    suspend fun downloadExploreCover(accessToken: String, coverPath: String): ByteArray = withContext(Dispatchers.IO) {
        val url = if (coverPath.startsWith("http")) coverPath else "$baseUrl$coverPath"
        val connection = (URL(url).openConnection() as HttpURLConnection).apply {
            requestMethod = "GET"
            connectTimeout = 30_000
            readTimeout = 30_000
            setRequestProperty("Authorization", "Bearer $accessToken")
        }
        try {
            val status = connection.responseCode
            if (status !in 200..299) throw ApiException(status, "Cover artwork could not be loaded ($status).")
            connection.inputStream.use { it.readBytes() }
        } finally {
            connection.disconnect()
        }
    }

    private fun detectImageContentType(bytes: ByteArray): String = when {
        bytes.size >= 8 && bytes.sliceArray(0..7).contentEquals(byteArrayOf(0x89.toByte(), 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) -> "image/png"
        bytes.size >= 12 && String(bytes, 0, 4) == "RIFF" && String(bytes, 8, 4) == "WEBP" -> "image/webp"
        else -> "image/jpeg"
    }

    /** Keeps embedded audiobook artwork below the API's 2 MB safety limit. */
    private fun prepareCoverForUpload(bytes: ByteArray): ByteArray {
        val targetBytes = 1_900_000
        if (bytes.size <= targetBytes) return bytes
        val source = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
            ?: throw IOException("The embedded cover artwork could not be resized.")
        val largestSide = maxOf(source.width, source.height).coerceAtLeast(1)
        val scale = minOf(1f, 1_600f / largestSide)
        val resized = if (scale < 1f) {
            Bitmap.createScaledBitmap(
                source,
                (source.width * scale).toInt().coerceAtLeast(1),
                (source.height * scale).toInt().coerceAtLeast(1),
                true,
            )
        } else source
        try {
            var quality = 90
            var compressed: ByteArray
            do {
                compressed = ByteArrayOutputStream().use { output ->
                    resized.compress(Bitmap.CompressFormat.JPEG, quality, output)
                    output.toByteArray()
                }
                quality -= 10
            } while (compressed.size > targetBytes && quality >= 50)
            if (compressed.size > targetBytes) throw IOException("The embedded cover artwork is too large to save.")
            return compressed
        } finally {
            if (resized !== source) resized.recycle()
            source.recycle()
        }
    }

    suspend fun saveBook(accessToken: String, request: LibraryBookUpsertRequest): LibraryBook =
        request("PUT", "/v1/library", json.encodeToString(request), accessToken)

    suspend fun uploadEmbeddedCover(
        accessToken: String,
        fingerprint: BookFingerprint,
        bytes: ByteArray,
    ) {
        val uploadBytes = prepareCoverForUpload(bytes)
        request<Unit>(
            "PUT",
            "/v1/import/cover",
            json.encodeToString(
                EmbeddedCoverUploadRequest(
                    fingerprint,
                    detectImageContentType(uploadBytes),
                    Base64.encodeToString(uploadBytes, Base64.NO_WRAP),
                ),
            ),
            accessToken,
        )
    }

    suspend fun deleteBook(accessToken: String, bookID: String) {
        request<Unit>("DELETE", "/v1/library/$bookID", null, accessToken)
    }

    suspend fun updateBookDetails(
        accessToken: String,
        bookID: String,
        details: LibraryBookDetailsRequest,
    ): LibraryBook = request(
        "PUT", "/v1/library/$bookID/details", json.encodeToString(details), accessToken,
    )

    suspend fun reportEditionSignature(accessToken: String, report: EditionSignatureReportRequest) {
        request<Unit>(
            "POST",
            "/v1/editions/signatures",
            json.encodeToString(report),
            accessToken,
        )
    }
    suspend fun reportEditionDescription(
        accessToken: String,
        report: EditionDescriptionReportRequest,
    ) {
        request<Unit>(
            "POST",
            "/v1/editions/descriptions",
            json.encodeToString(report),
            accessToken,
        )
    }

    suspend fun saveProgress(accessToken: String, bookID: String, positionSeconds: Double, isFinished: Boolean): LibraryBook =
        request(
            "PUT",
            "/v1/library/$bookID/progress",
            json.encodeToString(PlaybackProgressRequest(positionSeconds, isFinished)),
            accessToken,
        )

    suspend fun bookmarks(accessToken: String, bookID: String): List<LibraryBookmark> =
        request("GET", "/v1/library/$bookID/bookmarks", null, accessToken)

    suspend fun addBookmark(accessToken: String, bookID: String, positionSeconds: Double): LibraryBookmark =
        post(
            "/v1/library/$bookID/bookmarks",
            BookmarkCreateRequest(positionSeconds, title = "Bookmark at ${formatBookmarkTime(positionSeconds)}"),
            accessToken,
        )

    suspend fun deleteBookmark(accessToken: String, bookmarkID: String) {
        request<Unit>("DELETE", "/v1/library/bookmarks/$bookmarkID", null, accessToken)
    }

    suspend fun bookFilterSettings(accessToken: String, bookID: String): BookFilterSettings =
        request("GET", "/v1/library/$bookID/filter-settings", null, accessToken)

    suspend fun saveBookFilterSettings(
        accessToken: String,
        bookID: String,
        settings: BookFilterSettingsUpsertRequest,
    ): BookFilterSettings = request(
        "PUT", "/v1/library/$bookID/filter-settings", json.encodeToString(settings), accessToken,
    )

    /**
     * Tells the server that filtering was wrong at a particular moment.
     *
     * The only route by which a missed passage or an over-zealous skip becomes something
     * anyone can act on.
     */
    suspend fun reportFilter(
        accessToken: String,
        report: FilterReportRequest,
    ): FilterReportAcknowledgement =
        post("/v1/filter-reports", report, accessToken)

    private fun formatBookmarkTime(seconds: Double): String {
        val value = seconds.toLong()
        return "%d:%02d:%02d".format(value / 3600, (value % 3600) / 60, value % 60)
    }

    suspend fun findScan(accessToken: String, fingerprint: BookFingerprint): CloudScanResponse =
        post("/v1/scans/requests", CloudScanRequest(fingerprint), accessToken, scanChannel())

    /**
     * What this account is entitled to.
     *
     * A GET, so the shared retry in [request] already covers a flaky connection. That is why
     * the narration tier store adds none of its own: a failure here is answered by the grace
     * period, not by asking again immediately.
     */
    suspend fun accountAccess(accessToken: String): AccountAccessResponse =
        request("GET", "/v1/account/access", null, accessToken)

    /** Voices the server offers, with the premium synthesis agreement. */
    suspend fun narrationVoices(accessToken: String): NarrationVoicesResponse =
        request("GET", "/v1/narration/voices", null, accessToken)

    /** Records the listener's acceptance of the premium synthesis agreement. */
    suspend fun acceptNarrationAgreement(
        accessToken: String,
        request: NarrationAcknowledgementRequest,
    ): NarrationAcknowledgementResponse =
        post("/v1/narration/acknowledgements", request, accessToken)

    /**
     * Submits one chapter for premium synthesis.
     *
     * Returns immediately with a job to poll. A chapter can hold twenty thousand characters, which
     * is minutes of work, so holding an HTTP request open for it would exceed every sensible
     * timeout on the way.
     */
    suspend fun submitNarrationChapter(
        accessToken: String,
        request: NarrationChapterRequest,
    ): NarrationChapterAccepted =
        post("/v1/narration/chapters", request, accessToken)

    /** Polls one chapter job. Carries the audio once it is finished. */
    suspend fun narrationChapter(accessToken: String, jobID: String): NarrationChapterStatus =
        request("GET", "/v1/narration/chapters/$jobID", null, accessToken)

    /**
     * Asks the server to find filterable content in an EPUB's text.
     *
     * Reaches the network at most once per book: the caller reuses a stored scan while the
     * text is unchanged. Not retried here, in line with every other POST on this class --
     * the retry that this endpoint does need is in `TextScanClient`, where it can tell a
     * timeout worth repeating from a refusal that never will be.
     */
    suspend fun narrationTextScan(
        accessToken: String,
        request: NarrationTextScanRequest,
    ): NarrationTextScanResponse = post("/v1/narration/text-scans", request, accessToken)

    suspend fun createReaderAlignment(
        accessToken: String,
        bookID: String,
        epubText: String,
    ): ReaderAlignmentResponse = post(
        "/v1/reader/alignments",
        ReaderAlignmentRequest(bookID, epubText),
        accessToken,
    )

    /** Gets the pre-approved filter result for one published Beta edition.
     * This does not create an upload, transcript, or cloud scan. */
    suspend fun exploreFilterResult(accessToken: String, catalogID: String): CloudScanResponse =
        request("GET", "/v1/explore/$catalogID/filter-result", null, accessToken)

    suspend fun authorizeUpload(
        accessToken: String,
        fingerprint: BookFingerprint,
        fileName: String,
        contentType: String,
    ): CloudUploadAuthorizationResponse = post(
        "/v1/uploads/authorizations",
        CloudUploadAuthorizationRequest(fingerprint, fileName, contentType, fingerprint.fileSize),
        accessToken,
    )

    suspend fun completeUpload(accessToken: String, uploadID: String) {
        request<Unit>("POST", "/v1/uploads/$uploadID/complete", null, accessToken)
    }

    suspend fun claimCompanionTransfer(accessToken: String, transferID: String, code: String): CompanionTransferClaimResponse =
        request(
            "GET",
            "/v1/companion/transfers/$transferID/claim?code=${URLEncoder.encode(code, Charsets.UTF_8.name())}",
            null,
            accessToken,
        )

    suspend fun completeCompanionTransfer(accessToken: String, transferID: String) {
        request<Unit>("POST", "/v1/companion/transfers/$transferID/received", null, accessToken)
    }

    suspend fun submitScan(accessToken: String, uploadID: String, fingerprint: BookFingerprint): CloudScanResponse =
        post("/v1/scans/jobs", CloudScanJobSubmissionRequest(uploadID, fingerprint), accessToken, scanChannel())

    private fun scanChannel(): String? = if (BuildConfig.BETA_BUILD) "ios-beta" else null

    suspend fun scanJob(accessToken: String, scanID: String): CloudScanResponse =
        request("GET", "/v1/scans/jobs/$scanID", null, accessToken)

    suspend fun uploadAudio(
        authorization: CloudUploadAuthorizationResponse,
        resolver: ContentResolver,
        uri: Uri,
        expectedSize: Long,
        onProgress: (Float) -> Unit = {},
    ) = withContext(Dispatchers.IO) {
        if (authorization.headers.keys.none { it.equals("x-ms-blob-type", ignoreCase = true) }) {
            uploadSingleRequest(authorization, resolver, uri, expectedSize)
            onProgress(1f)
            return@withContext
        }
        val blockSize = 8 * 1024 * 1024
        val blockIDs = mutableListOf<String>()
        var uploaded = 0L
        resolver.openInputStream(uri).use { input ->
            requireNotNull(input) { "The selected audiobook is no longer available." }
            val buffer = ByteArray(blockSize)
            var blockIndex = 0
            while (true) {
                var count = 0
                while (count < buffer.size) {
                    val read = input.read(buffer, count, buffer.size - count)
                    if (read < 0) break
                    count += read
                }
                if (count == 0) break
                val blockID = Base64.encodeToString(
                    "%08d".format(blockIndex).toByteArray(),
                    Base64.NO_WRAP,
                )
                val blockURL = appendQuery(
                    authorization.uploadURL,
                    "comp=block&blockid=${URLEncoder.encode(blockID, Charsets.UTF_8.name())}",
                )
                uploadBlockWithRetry(blockURL, authorization.headers, buffer, count)
                blockIDs += blockID
                uploaded += count
                onProgress((uploaded.toDouble() / expectedSize.coerceAtLeast(1)).toFloat().coerceIn(0f, 1f))
                blockIndex += 1
            }
        }
        require(uploaded == expectedSize) { "The selected audiobook changed while it was uploading." }
        commitBlocks(authorization.uploadURL, authorization.headers, blockIDs)
        onProgress(1f)
    }

    private fun uploadSingleRequest(
        authorization: CloudUploadAuthorizationResponse,
        resolver: ContentResolver,
        uri: Uri,
        expectedSize: Long,
    ) {
        val connection = (URL(authorization.uploadURL).openConnection() as HttpURLConnection).apply {
            requestMethod = authorization.method
            connectTimeout = 60_000
            // Large audiobooks can legitimately take many minutes on a mobile
            // connection. The server authorization is short-lived, but the
            // upload itself must not be capped at the old 90-second timeout.
            readTimeout = 30 * 60 * 1000
            doOutput = true
            setFixedLengthStreamingMode(expectedSize)
            authorization.headers.forEach { (name, value) -> setRequestProperty(name, value) }
        }
        try {
            resolver.openInputStream(uri).use { input ->
                requireNotNull(input) { "The selected audiobook is no longer available." }
                connection.outputStream.use { output -> input.copyTo(output, 1024 * 1024) }
            }
            val status = connection.responseCode
            if (status !in 200..299) throw ApiException(status, "The private audiobook upload failed ($status).")
        } finally {
            connection.disconnect()
        }
    }

    /**
     * Retrying a single block is safe: re-uploading the same block ID replaces
     * it rather than appending, so Blob block PUTs are idempotent.
     */
    private suspend fun uploadBlockWithRetry(url: String, headers: Map<String, String>, bytes: ByteArray, count: Int) {
        var lastFailure: Throwable? = null
        repeat(4) { attempt ->
            try {
                putBytes(url, headers, bytes, count, "application/octet-stream")
                return
            } catch (cancellation: kotlinx.coroutines.CancellationException) {
                // Must propagate, or cancelling an upload silently keeps retrying.
                throw cancellation
            } catch (failure: Throwable) {
                lastFailure = failure
                // delay() is cancellable; the previous Thread.sleep parked an IO
                // thread and ignored cancellation entirely.
                if (attempt < 3) kotlinx.coroutines.delay(1_000L shl attempt)
            }
        }
        throw IOException("The audiobook upload was interrupted after several automatic retries.", lastFailure)
    }

    private fun commitBlocks(url: String, headers: Map<String, String>, blockIDs: List<String>) {
        val xml = buildString {
            append("<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList>")
            blockIDs.forEach { append("<Latest>").append(it).append("</Latest>") }
            append("</BlockList>")
        }.toByteArray(Charsets.UTF_8)
        putBytes(appendQuery(url, "comp=blocklist"), headers, xml, xml.size, "application/xml")
    }

    private fun putBytes(url: String, headers: Map<String, String>, bytes: ByteArray, count: Int, contentType: String) {
        val connection = (URL(url).openConnection() as HttpURLConnection).apply {
            requestMethod = "PUT"
            connectTimeout = 60_000
            // Each 8 MiB block is independently retried; allow slow mobile
            // networks enough time to finish a block before retrying it.
            readTimeout = 10 * 60 * 1000
            doOutput = true
            setFixedLengthStreamingMode(count)
            headers.filterKeys { !it.equals("x-ms-blob-type", ignoreCase = true) }
                .forEach { (name, value) -> setRequestProperty(name, value) }
            setRequestProperty("Content-Type", contentType)
        }
        try {
            connection.outputStream.use { it.write(bytes, 0, count) }
            val status = connection.responseCode
            if (status !in 200..299) {
                val detail = connection.errorStream?.bufferedReader()?.use { it.readText() }.orEmpty()
                throw ApiException(status, "The private audiobook upload failed ($status). $detail".trim())
            }
        } finally {
            connection.disconnect()
        }
    }

    private fun appendQuery(url: String, query: String): String =
        "$url${if (url.contains('?')) '&' else '?'}$query"

    private suspend inline fun <reified Request : Any, reified Response> post(
        path: String,
        body: Request,
        token: String? = null,
        scanChannel: String? = null,
    ): Response = request("POST", path, json.encodeToString(body), token, scanChannel)

    private suspend inline fun <reified Response> request(
        method: String,
        path: String,
        body: String?,
        token: String?,
        scanChannel: String? = null,
    ): Response = withContext(Dispatchers.IO) {
        check(baseUrl.startsWith("https://")) {
            "AudioChoice staging has not been connected to this build yet."
        }
        // Only GET is safe to replay automatically. A POST/PUT/DELETE whose
        // response read failed may already have been applied on the server -- a
        // very common mobile failure mode -- and replaying it produced duplicate
        // bookmarks, duplicate consent rows, and duplicate paid scan jobs. For
        // those verbs the failure is surfaced so the caller can offer a retry.
        val maximumAttempts = if (method == "GET") 3 else 1
        var lastConnectionError: IOException? = null
        repeat(maximumAttempts) { attempt ->
            val connection = (URL("$baseUrl$path").openConnection() as HttpURLConnection).apply {
                requestMethod = method
                connectTimeout = 45_000
                readTimeout = 90_000
                setRequestProperty("Accept", "application/json")
                if (token != null) setRequestProperty("Authorization", "Bearer $token")
                if (scanChannel != null) setRequestProperty("X-AudioChoice-Scan-Channel", scanChannel)
                if (body != null) {
                    doOutput = true
                    setRequestProperty("Content-Type", "application/json")
                }
            }
            try {
                if (body != null) connection.outputStream.bufferedWriter().use { it.write(body) }
                val status = connection.responseCode
                val responseText = (if (status in 200..299) connection.inputStream else connection.errorStream)
                    ?.bufferedReader()?.use { it.readText() }.orEmpty()
                if (status !in 200..299) {
                    val detail = runCatching { json.decodeFromString<ApiError>(responseText).error }.getOrNull()
                    // A 401 on an authenticated, non-sign-in endpoint means the
                    // session died rather than that a password was wrong.
                    val expiredSession = status == 401 && token != null && !isAuthenticationPath(path)
                    if (expiredSession) mutableSessionExpired.tryEmit(Unit)
                    throw ApiException(
                        status,
                        if (expiredSession) {
                            "Your AudioChoice session has expired. Please sign in again."
                        } else {
                            detail ?: when (status) {
                                401 -> "The email or password was not accepted."
                                409 -> "That account is already linked."
                                else -> "AudioChoice could not complete that request ($status)."
                            }
                        },
                    )
                }
                @Suppress("UNCHECKED_CAST")
                return@withContext if (Response::class == Unit::class || responseText.isBlank()) Unit as Response
                else json.decodeFromString<Response>(responseText)
            } catch (error: IOException) {
                lastConnectionError = error
                if (attempt < maximumAttempts - 1) {
                    kotlinx.coroutines.delay(((attempt + 1) * 1_500).toLong())
                }
            } finally {
                connection.disconnect()
            }
        }
        throw IOException(
            "AudioChoice could not reach the private scan service. Check your internet connection and tap Retry.",
            lastConnectionError,
        )
    }
}
