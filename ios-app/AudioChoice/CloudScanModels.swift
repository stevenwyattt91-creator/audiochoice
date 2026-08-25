import Foundation

enum CloudScanStatus: String, Codable {
    case available, uploadRequired, queued, processing, completed, failed
}

struct BookFingerprint: Codable, Equatable {
    let version: Int
    let sha256: String
    let fileSize: Int64
    let duration: Double?
    let fileType: String
    let workTitle: String?
    let author: String?
    let seriesTitle: String?
    let seriesNumber: Int?
    let editionType: String?
    let partNumber: Int?
    let totalParts: Int?
}

struct ScanEvent: Codable, Identifiable, Equatable {
    let id: UUID
    let startTime: Double
    let endTime: Double
    let categoryID: UUID
    let groupID: UUID
    let eventID: UUID
    let confidence: Double
    let stableKey: String?
    let safeDescription: String?
    let aggregateKey: String?
    let aggregateDisplay: String?
}

struct ScanResult: Codable, Equatable {
    let events: [ScanEvent]
    let scanDate: Date
    let scannerVersion: String
}

struct CloudScanRequest: Codable {
    let fingerprint: BookFingerprint
    let currentScannerVersion: String?
}

struct CloudScanResponse: Codable {
    let status: CloudScanStatus
    let scanID: UUID?
    let result: ScanResult?
    let taxonomyVersion: String?
    let progressPercent: Int
    let progressStage: String?
    let completedChunks: Int
    let totalChunks: Int
    let percentComplete: Int

    private enum CodingKeys: String, CodingKey {
        case status, scanID, result, taxonomyVersion, progressPercent, progressStage
        case completedChunks, totalChunks, percentComplete
    }

    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        status = try values.decode(CloudScanStatus.self, forKey: .status)
        scanID = try values.decodeIfPresent(UUID.self, forKey: .scanID)
        result = try values.decodeIfPresent(ScanResult.self, forKey: .result)
        taxonomyVersion = try values.decodeIfPresent(String.self, forKey: .taxonomyVersion)
        progressPercent = try values.decodeIfPresent(Int.self, forKey: .progressPercent) ?? 0
        progressStage = try values.decodeIfPresent(String.self, forKey: .progressStage)
        completedChunks = try values.decodeIfPresent(Int.self, forKey: .completedChunks) ?? 0
        totalChunks = try values.decodeIfPresent(Int.self, forKey: .totalChunks) ?? 0
        percentComplete = try values.decodeIfPresent(Int.self, forKey: .percentComplete) ?? progressPercent
    }
}

struct CloudUploadAuthorizationRequest: Codable {
    let fingerprint: BookFingerprint
    let fileName: String
    let contentType: String
    let fileSize: Int64
}

struct CloudUploadAuthorizationResponse: Codable {
    let uploadID: UUID
    let uploadURL: URL
    let method: String
    let headers: [String: String]
    let expiresAt: Date
}

struct CloudScanJobSubmissionRequest: Codable {
    let uploadID: UUID
    let fingerprint: BookFingerprint
}

struct AccountLibraryBook: Codable, Identifiable {
    let id: UUID
    let fingerprint: BookFingerprint
    let title: String
    let author: String?
    let narrator: String?
    let coverImageURL: String?
    let playbackPositionSeconds: Double
    let isFinished: Bool
    let isFavorite: Bool
    let addedAt: Date
    let updatedAt: Date
}

struct LibraryBookUpsertRequest: Codable {
    let fingerprint: BookFingerprint
    let title: String
    let author: String?
    let narrator: String?
    let coverImageURL: String?
}

struct PlaybackProgressRequest: Codable {
    let positionSeconds: Double
    let isFinished: Bool
}

struct SupportMessageRequest: Codable {
    let subject: String
    let message: String
}

struct SupportMessageResponse: Codable {
    let status: String
}

struct ExploreCatalogBook: Codable, Identifiable {
    var id: String { catalogID }
    let catalogID: String
    let title: String
    let author: String?
    let editionType: String?
    let duration: Double?
    let fileType: String
    let eventCount: Int
    let coverImageURL: String?
    let description: String?
    let purchaseURL: URL
    let purchaseProvider: String
}
