import Foundation

struct FilterCategory: Identifiable, Codable {

    let id: UUID

    /// Name shown in the UI.
    var displayName: String

    /// Groups within this category.
    var groups: [FilterGroup]

    /// Enables or disables the entire category.
    var isEnabled: Bool
    mutating func setEnabled(
        _ enabled: Bool
    ) {
        isEnabled = enabled

        for index in groups.indices {

            groups[index].setEnabled(enabled)
        }
    }

    init(
        id: UUID = UUID(),
        displayName: String,
        groups: [FilterGroup] = [],
        isEnabled: Bool = true
    ) {
        self.id = id
        self.displayName = displayName
        self.groups = groups
        self.isEnabled = isEnabled
    }

    // MARK: - Statistics

    var groupCount: Int {
        groups.count
    }

    var eventCount: Int {
        groups.reduce(0) {
            $0 + $1.eventCount
        }
    }

    var enabledEventCount: Int {
        groups.reduce(0) {
            $0 + $1.enabledEventCount
        }
    }

    var totalDuration: TimeInterval {
        groups.reduce(0) {
            $0 + $1.totalDuration
        }
    }

    var enabledDuration: TimeInterval {
        groups.reduce(0) {
            $0 + $1.enabledDuration
        }
    }
    mutating func refreshEnabledState() {

        isEnabled = groups.allSatisfy {
            $0.isEnabled
        }
    }
}
