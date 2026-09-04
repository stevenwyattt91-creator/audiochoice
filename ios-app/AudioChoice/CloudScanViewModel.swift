import Foundation

@MainActor
final class CloudScanViewModel: ObservableObject {
    enum Phase: Equatable {
        case idle, reading, fingerprinting, searching, uploading, queued, processing, complete, failed
    }

    @Published private(set) var phase: Phase = .idle
    @Published private(set) var result: ScanResult?
    @Published private(set) var errorMessage: String?
    @Published private(set) var uploadProgress = 0
    @Published private(set) var analysisProgress = 0
    @Published private(set) var completedChunks = 0
    @Published private(set) var totalChunks = 0
    @Published private(set) var analysisStage = "Preparing analysis"
    @Published private(set) var isReconnecting = false
    @Published private(set) var reconnectAttempt = 0
    @Published private(set) var connectionStatus: String?

    /// Runs a scan for [record].
    ///
    /// [isRescan] distinguishes scanning a book already in the library from finishing an import.
    /// The difference matters a great deal: an incomplete *import* must not leave a playable,
    /// unfiltered book behind, so a failure discards it. A failed *rescan* must leave the book
    /// exactly as it was -- the listener already had it, and destroying a book and its audio file
    /// because a scan could not be completed would be a far worse outcome than the missing filter
    /// data they asked to fix.
    func start(fileURL: URL, record: LibraryBookRecord, isRescan: Bool = false) async {
        guard phase == .idle || phase == .failed else { return }
        errorMessage = nil
        uploadProgress = 0
        analysisProgress = 0
        completedChunks = 0
        totalChunks = 0
        isReconnecting = false
        reconnectAttempt = 0
        connectionStatus = nil
        let hasAccess = fileURL.startAccessingSecurityScopedResource()
        defer { if hasAccess { fileURL.stopAccessingSecurityScopedResource() } }

        do {
            phase = .reading
            _ = try fileURL.resourceValues(forKeys: [.isReadableKey])
            phase = .fingerprinting
            let fingerprint = try await AudiobookFingerprintService().fingerprint(fileURL: fileURL)
            while !Task.isCancelled {
                do {
                    let client = try CloudScanClient.configured()
                    let latest = AudiobookLibraryStore.load().first(where: { $0.id == record.id }) ?? record
                    if reconnectAttempt > 0 {
                        connectionStatus = "Backend retry attempt \(reconnectAttempt)…"
                    }
                    if let pendingScanID = latest.pendingScanID {
                        phase = latest.scanState == CloudScanStatus.processing.rawValue ? .processing : .queued
                        let pending = try await client.job(scanID: pendingScanID)
                        markReconnectedIfNeeded()
                        result = try await resolveJob(pending, client: client, bookID: record.id)
                    } else {
                        phase = .searching
                        // AudiobookImportService already read this file's container tags and
                        // built this signature at import time, before this lookup ever runs.
                        // Sent here so THIS lookup can use it to recognise a converted or
                        // re-tagged copy of an edition already scanned, rather than the
                        // evidence only reaching the server afterward through LibraryScreen's
                        // separate, unrelated library-row upsert.
                        let lookup = try await client.requestScan(
                            CloudScanRequest(
                                fingerprint: fingerprint,
                                currentScannerVersion: latest.scanResult?.scannerVersion,
                                signature: latest.editionSignature
                            )
                        )
                        markReconnectedIfNeeded()
                        result = try await resolve(
                            lookup,
                            fileURL: fileURL,
                            fingerprint: fingerprint,
                            client: client,
                            bookID: record.id
                        )
                    }
                    if let result { AudiobookLibraryStore.attach(result: result, to: record.id) }
                    isReconnecting = false
                    connectionStatus = reconnectAttempt > 0 ? "Reconnected — scan complete" : nil
                    phase = .complete
                    return
                } catch is CancellationError {
                    throw CancellationError()
                } catch where isRecoverableNetworkError(error) {
                    reconnectAttempt += 1
                    isReconnecting = true
                    phase = .failed
                    errorMessage = "The network connection was lost. AudioChoice is retrying automatically."
                    connectionStatus = "Waiting for a connection • retry \(reconnectAttempt)"
                    let delay = min(15, 2 + reconnectAttempt * 2)
                    try await Task.sleep(for: .seconds(delay))
                    errorMessage = nil
                } catch {
                    if !isRescan { discardIncompleteImportIfNeeded(record) }
                    errorMessage = error.localizedDescription
                    phase = .failed
                    return
                }
            }
        } catch is CancellationError {
            phase = .idle
            Task { await ScanRecoveryManager.shared.recoverPendingScans() }
        } catch {
            if !isRescan { discardIncompleteImportIfNeeded(record) }
            errorMessage = error.localizedDescription
            phase = .failed
        }
    }

    private func markReconnectedIfNeeded() {
        guard reconnectAttempt > 0 else { return }
        isReconnecting = false
        connectionStatus = "Reconnected — resuming scan"
    }

    private func isRecoverableNetworkError(_ error: Error) -> Bool {
        let value = error as NSError
        if value.domain == NSURLErrorDomain {
            return [
                NSURLErrorTimedOut,
                NSURLErrorCannotFindHost,
                NSURLErrorCannotConnectToHost,
                NSURLErrorNetworkConnectionLost,
                NSURLErrorDNSLookupFailed,
                NSURLErrorNotConnectedToInternet,
                NSURLErrorInternationalRoamingOff,
                NSURLErrorDataNotAllowed,
                NSURLErrorCallIsActive
            ].contains(value.code)
        }
        if let underlying = value.userInfo[NSUnderlyingErrorKey] as? Error {
            return isRecoverableNetworkError(underlying)
        }
        return false
    }

    private func discardIncompleteImportIfNeeded(_ original: LibraryBookRecord) {
        // A job that already reached the server remains recoverable after a temporary
        // connection interruption. Everything else is an incomplete import and must
        // not become a playable, unfiltered library book.
        guard original.scanResult == nil else { return }
        let latest = AudiobookLibraryStore.load().first(where: { $0.id == original.id }) ?? original
        guard latest.pendingScanID == nil else { return }
        AudiobookLibraryStore.remove(latest)
    }

    private func resolve(
        _ response: CloudScanResponse,
        fileURL: URL,
        fingerprint: BookFingerprint,
        client: CloudScanClient,
        bookID: UUID
    ) async throws -> ScanResult {
        switch response.status {
        case .available, .completed:
            guard let result = response.result else { throw CloudClientError.missingResult }
            return result
        case .uploadRequired:
            phase = .uploading
            let authorization = try await client.authorizeUpload(
                CloudUploadAuthorizationRequest(
                    fingerprint: fingerprint,
                    fileName: fileURL.lastPathComponent,
                    contentType: fingerprint.fileType,
                    fileSize: fingerprint.fileSize
                )
            )
            try await client.upload(fileURL: fileURL, authorization: authorization) { progress in
                Task { @MainActor in
                    self.uploadProgress = Int((progress * 100).rounded()).clamped(to: 0...100)
                }
            }
            uploadProgress = 100
            try await client.completeUpload(uploadID: authorization.uploadID)
            phase = .queued
            let submission = try await client.submitJob(
                CloudScanJobSubmissionRequest(uploadID: authorization.uploadID, fingerprint: fingerprint)
            )
            return try await resolveJob(submission, client: client, bookID: bookID)
        case .queued, .processing:
            return try await resolveJob(response, client: client, bookID: bookID)
        case .failed:
            throw CloudClientError.scanFailed
        }
    }

    private func resolveJob(_ initial: CloudScanResponse, client: CloudScanClient, bookID: UUID) async throws -> ScanResult {
        updateProgress(from: initial)
        if initial.status == .completed || initial.status == .available {
            guard let result = initial.result else { throw CloudClientError.missingResult }
            return result
        }
        guard let scanID = initial.scanID else { throw CloudClientError.missingScanID }
        AudiobookLibraryStore.setPendingScan(id: scanID, state: initial.status, for: bookID)
        for _ in 0..<900 {
            try await Task.sleep(for: .seconds(2))
            let response = try await client.job(scanID: scanID)
            updateProgress(from: response)
            switch response.status {
            case .completed, .available:
                guard let result = response.result else { throw CloudClientError.missingResult }
                return result
            case .queued:
                phase = .queued
                AudiobookLibraryStore.setPendingScan(id: scanID, state: .queued, for: bookID)
            case .processing:
                phase = .processing
                AudiobookLibraryStore.setPendingScan(id: scanID, state: .processing, for: bookID)
            case .failed:
                AudiobookLibraryStore.setScanState(.failed, for: bookID)
                throw CloudClientError.scanFailed
            case .uploadRequired:
                throw CloudClientError.scanFailed
            }
        }
        throw CloudClientError.timedOut
    }

    private func updateProgress(from response: CloudScanResponse) {
        analysisProgress = max(response.progressPercent, response.percentComplete).clamped(to: 0...100)
        completedChunks = max(0, response.completedChunks)
        totalChunks = max(0, response.totalChunks)
        if let stage = response.progressStage, !stage.isEmpty {
            analysisStage = stage.replacingOccurrences(of: "_", with: " ").capitalized
        } else if response.status == .queued {
            analysisStage = "Waiting securely"
        } else {
            analysisStage = "Transcribing and analyzing"
        }
    }
}

private extension Comparable {
    func clamped(to limits: ClosedRange<Self>) -> Self {
        min(max(self, limits.lowerBound), limits.upperBound)
    }
}
