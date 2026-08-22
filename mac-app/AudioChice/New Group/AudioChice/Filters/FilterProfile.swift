import Foundation

struct FilterProfile: Codable {

    /// Reserved for future cloud matching.
    var editionID: UUID?

    /// Categories contained in this profile.
    var categories: [FilterCategory]

    init(
        editionID: UUID? = nil,
        categories: [FilterCategory] = []
    ) {
        self.editionID = editionID
        self.categories = categories
    }

    // MARK: - Statistics

    var categoryCount: Int {
        categories.count
    }

    var eventCount: Int {
        categories.reduce(0) {
            $0 + $1.eventCount
        }
    }

    var enabledEventCount: Int {
        categories.reduce(0) {
            $0 + $1.enabledEventCount
        }
    }

    var totalDuration: TimeInterval {
        categories.reduce(0) {
            $0 + $1.totalDuration
        }
    }

    var enabledDuration: TimeInterval {
        categories.reduce(0) {
            $0 + $1.enabledDuration
        }
    }
}
