import Foundation

struct Chapter: Identifiable, Codable {
    let id: UUID
    var title: String
    var startTime: TimeInterval

    init(
        id: UUID = UUID(),
        title: String,
        startTime: TimeInterval
    ) {
        self.id = id
        self.title = title
        self.startTime = startTime
    }
}
