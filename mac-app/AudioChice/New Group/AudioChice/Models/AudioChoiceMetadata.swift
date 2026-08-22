import Foundation

struct AudioChoiceMetadata: Identifiable, Codable {
    let id: UUID
    var version: Int

    // Exact-edition identity
    var workTitle: String
    var author: String?
    var narrator: String?

    var seriesTitle: String?
    var seriesNumber: Int?

    var editionType: EditionType

    var partNumber: Int?
    var totalParts: Int?

    // Matching information
    var duration: TimeInterval
    var sourceFileType: String
    var sourceFingerprintSHA256: String?

    // Shared enhancements
    var chapters: [Chapter]

    // Future metadata corrections
    var correctedTitle: String?
    var correctedAuthor: String?
    var correctedNarrator: String?

    // Future filter-file identifier
    var filterProfileID: String?

    var createdAt: Date
    var updatedAt: Date

    init(
        id: UUID = UUID(),
        version: Int = 1,
        workTitle: String,
        author: String?,
        narrator: String?,
        seriesTitle: String?,
        seriesNumber: Int?,
        editionType: EditionType,
        partNumber: Int?,
        totalParts: Int?,
        duration: TimeInterval,
        sourceFileType: String,
        sourceFingerprintSHA256: String?,
        chapters: [Chapter],
        correctedTitle: String? = nil,
        correctedAuthor: String? = nil,
        correctedNarrator: String? = nil,
        filterProfileID: String? = nil,
        createdAt: Date = Date(),
        updatedAt: Date = Date()
    ) {
        self.id = id
        self.version = version

        self.workTitle = workTitle
        self.author = author
        self.narrator = narrator

        self.seriesTitle = seriesTitle
        self.seriesNumber = seriesNumber

        self.editionType = editionType

        self.partNumber = partNumber
        self.totalParts = totalParts

        self.duration = duration
        self.sourceFileType = sourceFileType
        self.sourceFingerprintSHA256 = sourceFingerprintSHA256

        self.chapters = chapters

        self.correctedTitle = correctedTitle
        self.correctedAuthor = correctedAuthor
        self.correctedNarrator = correctedNarrator

        self.filterProfileID = filterProfileID

        self.createdAt = createdAt
        self.updatedAt = updatedAt
    }
}
