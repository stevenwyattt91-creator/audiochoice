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

struct ReaderAlignmentRequest: Codable {
    let libraryBookID: UUID
    let epubText: String
}

struct ReaderAlignmentResponse: Codable {
    let ranges: [ReaderTimingRange]
}

/// Identity evidence about a recording that a file's byte hash cannot express.
///
/// Only a client can read this, since the server never sees container tags. A matching
/// retail product identifier is the one signal strong enough for the server to reuse
/// another copy's filter results.
struct EditionSignature: Codable, Equatable {
    var productIdentifier: String?
    var narrator: String?
    /// Chapter start offsets in whole seconds; survives re-encoding, because rewrapping
    /// a container does not move chapter marks.
    var chapterOffsetSeconds: [Int]?

    var isEmpty: Bool {
        (productIdentifier ?? "").isEmpty && (narrator ?? "").isEmpty && (chapterOffsetSeconds ?? []).isEmpty
    }
}

struct LibraryBookUpsertRequest: Codable {
    let fingerprint: BookFingerprint
    let title: String
    let author: String?
    let narrator: String?
    let coverImageURL: String?
    /// The fingerprint of the file actually on this device, when the row above adopts
    /// a different one. Lets the server link the two rather than losing track of
    /// artifacts stored under the local file's hash.
    var sourceFingerprint: BookFingerprint? = nil
    var signature: EditionSignature? = nil
    /// The publisher's synopsis, as read from this file's own description tags.
    ///
    /// Stored against the edition, so a well-tagged copy gives every other owner of that
    /// recording a real description in Explore instead of none.
    var description: String? = nil
}

/// Reports the synopsis for a book already in the library.
struct EditionDescriptionReportRequest: Codable {
    let fingerprint: BookFingerprint
    let description: String
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

/// A book's filter choices as the server stores them.
///
/// Categories and groups travel as GUIDs because the server's contract declares them
/// that way; the individual event and aggregate keys are opaque scanner strings.
struct BookFilterSettingsUpsertRequest: Codable {
    let disabledCategoryIDs: [UUID]
    let disabledGroupIDs: [UUID]
    let disabledEventKeys: [String]
    let disabledAggregateKeys: [String]
}

struct RemoteBookFilterSettings: Codable {
    let libraryBookID: UUID
    let disabledCategoryIDs: [UUID]
    let disabledGroupIDs: [UUID]
    let disabledEventKeys: [String]
    let disabledAggregateKeys: [String]
    let updatedAt: Date
}

/// A correction to how a book is labelled. Display only: the server deliberately keeps
/// these out of edition identification, which works from the file's own metadata.
struct LibraryBookDetailsRequest: Codable {
    let title: String
    var author: String? = nil
    var narrator: String? = nil
}

/// What a listener is telling us the filter got wrong.
///
/// Raw values match the server's camel-cased enum names.
enum FilterReportKind: String, Codable {
    /// Something played that should have been removed.
    case missedContent
    /// Something was removed that should have played.
    case wronglyFiltered
}

/// A report that filtering was wrong at a particular moment.
///
/// Carries a position and nothing about what was heard: no audio, no transcript text, no
/// words. The server already holds the transcript for this edition, so a timestamp is
/// enough to find the passage, and sending the content would undo the promise that a
/// listener's audio never leaves their device.
struct FilterReportRequest: Codable, Equatable {
    let fingerprint: BookFingerprint
    let kind: FilterReportKind
    let positionSeconds: Double
    /// How much audio before the tap this covers. A listener reacts, finds the button and
    /// taps, by which time the passage is already behind them.
    let windowSeconds: Double?
    /// Which scan produced the result, so a fixed scanner can be told from a bad match.
    let scannerVersion: String?
    /// Set when reporting a specific skip, which is what makes over-filtering actionable.
    let scanEventID: UUID?
    let categoryID: UUID?
}

/// One switched-off filter inside a saved profile.
///
/// The server's shape predates the per-book model, so it is generic: a key, whether it is
/// on, and two descriptive strings. Only `key` and `enabled` carry meaning here. Whether a
/// key names a category or a group is not stored, because the taxonomy already knows -- and
/// storing it would create a second source of truth that could disagree.
struct FilterRule: Codable, Equatable {
    let key: String
    let enabled: Bool
    let action: String
    let severity: String

    /// Everything is filtered unless a profile says otherwise, so a profile only ever
    /// records what was switched off.
    static func disabled(_ key: String) -> FilterRule {
        FilterRule(key: key, enabled: false, action: "skip", severity: "all")
    }
}

struct FilterProfileUpsertRequest: Codable {
    let name: String
    let isActive: Bool
    let rules: [FilterRule]
    let customWords: [String]
}

struct FilterProfile: Codable, Identifiable, Equatable {
    let id: UUID
    let name: String
    let isActive: Bool
    let rules: [FilterRule]
    let customWords: [String]
    let createdAt: Date
    let updatedAt: Date
}
