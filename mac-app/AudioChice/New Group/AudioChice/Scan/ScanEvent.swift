import Foundation

struct ScanEvent: Identifiable, Codable {

    let id: UUID

    var startTime: TimeInterval
    var endTime: TimeInterval

    var categoryID: UUID
    var groupID: UUID
    var eventID: UUID

    var confidence: Double

    init(
        id: UUID = UUID(),
        startTime: TimeInterval,
        endTime: TimeInterval,
        categoryID: UUID,
        groupID: UUID,
        eventID: UUID,
        confidence: Double = 1.0
    ) {
        self.id = id
        self.startTime = startTime
        self.endTime = endTime
        self.categoryID = categoryID
        self.groupID = groupID
        self.eventID = eventID
        self.confidence = confidence
    }
}
