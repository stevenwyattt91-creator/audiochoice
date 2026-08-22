import Foundation

struct Book: Identifiable, Codable {
    let id: UUID

    // Basic information
    var title: String
    var author: String?
    var narrator: String?

    // File information
    var originalFileURL: URL
    var fileType: String
    var convertedFileURL: URL?

    // Processing status
    var conversionStatus: ConversionStatus
    var scanStatus: ScanStatus

    // Listening information
    var currentPosition: TimeInterval
    var playbackSpeed: Double
    var lastPlayed: Date?
    var totalListeningTime: TimeInterval
    var isFinished: Bool

    // User-created information
    var bookmarks: [Bookmark]

    // Metadata
    var duration: TimeInterval?
    var coverArtData: Data?
    var chapters: [Chapter]
    var identity: BookIdentity?
    var fingerprint: BookFingerprint?
    var filterProfile: FilterProfile = FilterProfile()
    var scanResult: ScanResult?

    init(
        id: UUID = UUID(),
        title: String,
        originalFileURL: URL,
        fileType: String
    ) {
        self.id = id

        self.title = title
        self.author = nil
        self.narrator = nil

        self.originalFileURL = originalFileURL
        self.fileType = fileType
        self.convertedFileURL = nil

        self.conversionStatus =
            fileType.lowercased() == "aax"
            ? .waiting
            : .notNeeded

        self.scanStatus = .notScanned

        self.currentPosition = 0
        self.playbackSpeed = 1.0
        self.lastPlayed = nil
        self.totalListeningTime = 0
        self.isFinished = false

        self.bookmarks = []

        self.duration = nil
        self.coverArtData = nil
        self.chapters = []
        self.identity = nil
        self.fingerprint = nil
        self.filterProfile = FilterProfile()
        self.scanResult = nil
    }

    var progress: Double {
        guard let duration,
              duration > 0
        else {
            return 0
        }

        return min(
            max(currentPosition / duration, 0),
            1
        )
    }

    var remainingTime: TimeInterval {
        guard let duration else {
            return 0
        }

        return max(
            duration - currentPosition,
            0
        )
    }

    private enum CodingKeys: String, CodingKey {
        case id
        case title
        case author
        case narrator
        case originalFileURL
        case fileType
        case convertedFileURL
        case conversionStatus
        case scanStatus
        case currentPosition
        case playbackSpeed
        case lastPlayed
        case totalListeningTime
        case isFinished
        case bookmarks
        case duration
        case coverArtData
        case chapters
        case identity
        case fingerprint
        case filterProfile
        case scanResult
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(
            keyedBy: CodingKeys.self
        )

        id = try container.decode(UUID.self, forKey: .id)
        title = try container.decode(String.self, forKey: .title)

        author = try container.decodeIfPresent(
            String.self,
            forKey: .author
        )

        narrator = try container.decodeIfPresent(
            String.self,
            forKey: .narrator
        )

        originalFileURL = try container.decode(
            URL.self,
            forKey: .originalFileURL
        )

        fileType = try container.decode(
            String.self,
            forKey: .fileType
        )

        convertedFileURL = try container.decodeIfPresent(
            URL.self,
            forKey: .convertedFileURL
        )

        conversionStatus = try container.decode(
            ConversionStatus.self,
            forKey: .conversionStatus
        )

        scanStatus = try container.decode(
            ScanStatus.self,
            forKey: .scanStatus
        )

        currentPosition = try container.decodeIfPresent(
            TimeInterval.self,
            forKey: .currentPosition
        ) ?? 0

        playbackSpeed = try container.decodeIfPresent(
            Double.self,
            forKey: .playbackSpeed
        ) ?? 1.0

        lastPlayed = try container.decodeIfPresent(
            Date.self,
            forKey: .lastPlayed
        )

        totalListeningTime = try container.decodeIfPresent(
            TimeInterval.self,
            forKey: .totalListeningTime
        ) ?? 0

        isFinished = try container.decodeIfPresent(
            Bool.self,
            forKey: .isFinished
        ) ?? false

        bookmarks = try container.decodeIfPresent(
            [Bookmark].self,
            forKey: .bookmarks
        ) ?? []

        duration = try container.decodeIfPresent(
            TimeInterval.self,
            forKey: .duration
        )

        coverArtData = try container.decodeIfPresent(
            Data.self,
            forKey: .coverArtData
        )

        chapters = try container.decodeIfPresent(
            [Chapter].self,
            forKey: .chapters
        ) ?? []

        identity = try container.decodeIfPresent(
            BookIdentity.self,
            forKey: .identity
        )

        fingerprint = try container.decodeIfPresent(
            BookFingerprint.self,
            forKey: .fingerprint
        )

        filterProfile = try container.decodeIfPresent(
            FilterProfile.self,
            forKey: .filterProfile
        ) ?? FilterProfile()
        scanResult = try container.decodeIfPresent(
            ScanResult.self,
            forKey: .scanResult
        )
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)

        try container.encode(id, forKey: .id)
        try container.encode(title, forKey: .title)
        try container.encode(author, forKey: .author)
        try container.encode(narrator, forKey: .narrator)
        try container.encode(originalFileURL, forKey: .originalFileURL)
        try container.encode(fileType, forKey: .fileType)
        try container.encode(convertedFileURL, forKey: .convertedFileURL)
        try container.encode(conversionStatus, forKey: .conversionStatus)
        try container.encode(scanStatus, forKey: .scanStatus)
        try container.encode(currentPosition, forKey: .currentPosition)
        try container.encode(playbackSpeed, forKey: .playbackSpeed)
        try container.encode(lastPlayed, forKey: .lastPlayed)
        try container.encode(totalListeningTime, forKey: .totalListeningTime)
        try container.encode(isFinished, forKey: .isFinished)
        try container.encode(bookmarks, forKey: .bookmarks)
        try container.encode(duration, forKey: .duration)
        try container.encode(coverArtData, forKey: .coverArtData)
        try container.encode(chapters, forKey: .chapters)
        try container.encode(identity, forKey: .identity)
        try container.encode(fingerprint, forKey: .fingerprint)
        try container.encode(filterProfile, forKey: .filterProfile)
        try container.encode(scanResult, forKey: .scanResult)
    }
}
