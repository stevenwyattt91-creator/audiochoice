import Foundation

struct BookFingerprint: Codable, Equatable {
    var version: Int

    var sha256: String
    var fileSize: Int64
    var duration: TimeInterval?
    var fileType: String

    var workTitle: String?
    var author: String?
    var seriesTitle: String?
    var seriesNumber: Int?
    var editionType: EditionType?
    var partNumber: Int?
    var totalParts: Int?

    init(
        version: Int = 1,
        sha256: String,
        fileSize: Int64,
        duration: TimeInterval?,
        fileType: String,
        workTitle: String?,
        author: String?,
        seriesTitle: String?,
        seriesNumber: Int?,
        editionType: EditionType?,
        partNumber: Int?,
        totalParts: Int?
    ) {
        self.version = version
        self.sha256 = sha256
        self.fileSize = fileSize
        self.duration = duration
        self.fileType = fileType
        self.workTitle = workTitle
        self.author = author
        self.seriesTitle = seriesTitle
        self.seriesNumber = seriesNumber
        self.editionType = editionType
        self.partNumber = partNumber
        self.totalParts = totalParts
    }
}
