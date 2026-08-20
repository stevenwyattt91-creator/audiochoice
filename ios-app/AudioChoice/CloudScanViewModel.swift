import Foundation

@MainActor
final class CloudScanViewModel: ObservableObject {
    enum Phase: Equatable {
        case idle, reading, fingerprinting, searching, uploading, queued, processing, complete, failed
    }

    @Published private(set) var phase: Phase = .idle
    @Published private(set) var result: ScanResult?
    @Published private(set) var errorMessage: String?

    func start(fileURL: URL, record: LibraryBookRecord) async {
        guard phase == .idle || phase == .failed else { return }
        errorMessage = nil
        let hasAccess = fileURL.startAccessingSecurityScopedResource()
        defer { if hasAccess { fileURL.stopAccessingSecurityScopedResource() } }

        do {
            phase = .reading
            _ = try fileURL.resourceValues(forKeys: [.isReadableKey])
            phase = .fingerprinting
            let fingerprint = try await AudiobookFingerprintService().fingerprint(fileURL: fileURL)
            let client = try CloudScanClient.configured()
            phase = .searching
            let lookup = try await client.requestScan(
                CloudScanRequest(
                    fingerprint: fingerprint,
                    currentScannerVersion: record.scanResult?.scannerVersion
                )
            )
            result = try await resolve(
                lookup,
                fileURL: fileURL,
                fingerprint: fingerprint,
                client: client,
                bookID: record.id
            )
            if let result { AudiobookLibraryStore.attach(result: result, to: record.id) }
            phase = .complete
        } catch is CancellationError {
            phase = .idle
            Task { await ScanRecoveryManager.shared.recoverPendingScans() }
        } catch {
            errorMessage = error.localizedDescription
            phase = .failed
        }
    }

    func retry(fileURL: URL, record: LibraryBookRecord) async {
        phase = .idle
        await start(fileURL: fileURL, record: record)
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
            try await client.upload(fileURL: fileURL, authorization: authorization)
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
        if initial.status == .completed || initial.status == .available {
            guard let result = initial.result else { throw CloudClientError.missingResult }
            return result
        }
        guard let scanID = initial.scanID else { throw CloudClientError.missingScanID }
        AudiobookLibraryStore.setPendingScan(id: scanID, state: initial.status, for: bookID)
        for _ in 0..<900 {
            try await Task.sleep(for: .seconds(2))
            let response = try await client.job(scanID: scanID)
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
}
