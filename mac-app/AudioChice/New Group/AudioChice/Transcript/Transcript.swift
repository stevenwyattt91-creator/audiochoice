import Foundation

struct Transcript: Codable {

    var version: String

    var language: String

    var created: Date

    var segments: [TranscriptSegment]

    init(
        version: String = "1.0",
        language: String = "en",
        created: Date = Date(),
        segments: [TranscriptSegment] = []
    ) {
        self.version = version
        self.language = language
        self.created = created
        self.segments = segments
    }
}
