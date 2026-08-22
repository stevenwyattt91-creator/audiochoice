import Foundation

struct Bookmark: Identifiable, Codable, Equatable {
    let id: UUID

    var title: String
    var position: TimeInterval
    var note: String?
    var createdAt: Date

    init(
        id: UUID = UUID(),
        title: String,
        position: TimeInterval,
        note: String? = nil,
        createdAt: Date = Date()
    ) {
        self.id = id
        self.title = title
        self.position = position
        self.note = note
        self.createdAt = createdAt
    }
}
