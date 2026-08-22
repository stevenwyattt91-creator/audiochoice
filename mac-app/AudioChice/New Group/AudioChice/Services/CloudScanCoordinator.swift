import Foundation

struct CloudScanSource {
    var fileURL: URL
    var contentType: String
    var fingerprint: BookFingerprint
    var currentScannerVersion: String?

    var fileName: String {
        fileURL.lastPathComponent
    }
}

struct CloudScanCoordinatorConfiguration {
    var pollingInterval: TimeInterval
    var maximumPollingAttempts: Int

    init(
        pollingInterval: TimeInterval = 2,
        maximumPollingAttempts: Int = 300
    ) {
        self.pollingInterval = pollingInterval
        self.maximumPollingAttempts = maximumPollingAttempts
    }
}

protocol CloudScanPollingClock {
    func sleep(for interval: TimeInterval) async throws
}

struct TaskCloudScanPollingClock: CloudScanPollingClock {
    func sleep(for interval: TimeInterval) async throws {
        let nanoseconds = UInt64(
            max(interval, 0) * 1_000_000_000
        )

        try await Task.sleep(nanoseconds: nanoseconds)
    }
}

enum CloudScanCoordinatorError: LocalizedError {
    case invalidFileSize
    case missingScanID
    case missingResult
    case scanFailed
    case pollingTimedOut
    case unsupportedTaxonomy

    var errorDescription: String? {
        switch self {
        case .invalidFileSize:
            return "AudioChoice could not determine the audiobook file size."

        case .missingScanID:
            return "The cloud scan response did not include a scan ID."

        case .missingResult:
            return "The completed cloud scan did not include filter results."

        case .scanFailed:
            return "The cloud could not complete this audiobook scan."

        case .pollingTimedOut:
            return "AudioChoice stopped waiting for the cloud scan to finish."

        case .unsupportedTaxonomy:
            return "This scan uses a newer content taxonomy. Update AudioChoice to continue."
        }
    }
}

struct CloudScanCoordinator {
    private let scanService: any CloudScanService
    private let uploadService: any CloudAudioUploadService
    private let pollingClock: any CloudScanPollingClock
    private let configuration: CloudScanCoordinatorConfiguration

    init(
        scanService: any CloudScanService,
        uploadService: any CloudAudioUploadService,
        pollingClock: any CloudScanPollingClock =
            TaskCloudScanPollingClock(),
        configuration: CloudScanCoordinatorConfiguration =
            CloudScanCoordinatorConfiguration()
    ) {
        self.scanService = scanService
        self.uploadService = uploadService
        self.pollingClock = pollingClock
        self.configuration = configuration
    }

    func scan(
        source: CloudScanSource,
        uploadProgress: @escaping (CloudUploadProgress) -> Void
    ) async throws -> ScanResult {
        let lookup = try await scanService.requestScan(
            CloudScanRequest(
                fingerprint: source.fingerprint,
                currentScannerVersion: source.currentScannerVersion
            )
        )

        switch lookup.status {
        case .available, .completed:
            return try result(from: lookup)

        case .queued, .processing:
            guard let scanID = lookup.scanID else {
                throw CloudScanCoordinatorError.missingScanID
            }

            return try await poll(scanID: scanID)

        case .uploadRequired:
            return try await uploadAndScan(
                source: source,
                uploadProgress: uploadProgress
            )

        case .failed:
            throw CloudScanCoordinatorError.scanFailed
        }
    }

    private func uploadAndScan(
        source: CloudScanSource,
        uploadProgress: @escaping (CloudUploadProgress) -> Void
    ) async throws -> ScanResult {
        let fileSize = try source.fileURL.resourceValues(
            forKeys: [.fileSizeKey]
        ).fileSize

        guard let fileSize else {
            throw CloudScanCoordinatorError.invalidFileSize
        }

        let authorization = try await scanService
            .requestUploadAuthorization(
                CloudUploadAuthorizationRequest(
                    fingerprint: source.fingerprint,
                    fileName: source.fileName,
                    contentType: source.contentType,
                    fileSize: Int64(fileSize)
                )
            )

        try await uploadService.uploadAudio(
            from: source.fileURL,
            authorization: authorization,
            progress: uploadProgress
        )

        let submission = try await scanService.submitScanJob(
            CloudScanJobSubmissionRequest(
                uploadID: authorization.uploadID,
                fingerprint: source.fingerprint
            )
        )

        switch submission.status {
        case .available, .completed:
            return try result(from: submission)

        case .queued, .processing:
            guard let scanID = submission.scanID else {
                throw CloudScanCoordinatorError.missingScanID
            }

            return try await poll(scanID: scanID)

        case .failed:
            throw CloudScanCoordinatorError.scanFailed

        case .uploadRequired:
            throw CloudScanCoordinatorError.scanFailed
        }
    }

    private func poll(
        scanID: UUID
    ) async throws -> ScanResult {
        for attempt in 0..<configuration.maximumPollingAttempts {
            if attempt > 0 {
                try await pollingClock.sleep(
                    for: configuration.pollingInterval
                )
            }

            let response = try await scanService.scanStatus(
                scanID: scanID
            )

            switch response.status {
            case .available, .completed:
                return try result(from: response)

            case .failed:
                throw CloudScanCoordinatorError.scanFailed

            case .queued, .processing:
                continue

            case .uploadRequired:
                throw CloudScanCoordinatorError.scanFailed
            }
        }

        throw CloudScanCoordinatorError.pollingTimedOut
    }

    private func result(
        from response: CloudScanResponse
    ) throws -> ScanResult {
        guard let result = response.result else {
            throw CloudScanCoordinatorError.missingResult
        }

        if let taxonomyVersion = response.taxonomyVersion,
           taxonomyVersion != ContentTaxonomy.version {
            throw CloudScanCoordinatorError.unsupportedTaxonomy
        }

        if !result.events.allSatisfy(ContentTaxonomy.supports) {
            throw CloudScanCoordinatorError.unsupportedTaxonomy
        }

        return result
    }
}
