import Foundation

enum CloudScanStatus: String, Codable {
    case available
    case uploadRequired
    case queued
    case processing
    case completed
    case failed
}

struct CloudScanRequest: Codable {
    var fingerprint: BookFingerprint
    var currentScannerVersion: String?

    init(
        fingerprint: BookFingerprint,
        currentScannerVersion: String? = nil
    ) {
        self.fingerprint = fingerprint
        self.currentScannerVersion = currentScannerVersion
    }
}

struct CloudScanResponse: Codable {
    var status: CloudScanStatus
    var scanID: UUID?
    var result: ScanResult?
    var taxonomyVersion: String?

    init(
        status: CloudScanStatus,
        scanID: UUID? = nil,
        result: ScanResult? = nil,
        taxonomyVersion: String? = nil
    ) {
        self.status = status
        self.scanID = scanID
        self.result = result
        self.taxonomyVersion = taxonomyVersion
    }
}

struct CloudUploadAuthorizationRequest: Codable {
    var fingerprint: BookFingerprint
    var fileName: String
    var contentType: String
    var fileSize: Int64
}

enum CloudUploadMethod: String, Codable {
    case put = "PUT"
}

struct CloudUploadAuthorizationResponse: Codable {
    var uploadID: UUID
    var uploadURL: URL
    var method: CloudUploadMethod
    var headers: [String: String]
    var expiresAt: Date
}

struct CloudScanJobSubmissionRequest: Codable {
    var uploadID: UUID
    var fingerprint: BookFingerprint
}
