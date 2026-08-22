import Foundation

struct TranscriptSegment: Identifiable, Codable {

    let id: UUID

    var startTime: TimeInterval
    var endTime: TimeInterval

    var text: String

    init(
        id: UUID = UUID(),
        startTime: TimeInterval,
        endTime: TimeInterval,
        text: String
    ) {
        self.id = id
        self.startTime = startTime
        self.endTime = endTime
        self.text = text
    }
}
