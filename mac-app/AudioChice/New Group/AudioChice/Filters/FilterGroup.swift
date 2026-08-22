import Foundation

struct FilterGroup: Identifiable, Codable {

    let id: UUID

    /// Name shown to the user.
    var displayName: String

    /// Events contained in this group.
    var events: [FilterEvent]

    /// Default action for newly detected events.
    var defaultAction: FilterAction

    /// Default severity for newly detected events.
    var defaultSeverity: FilterSeverity

    /// Enables or disables the entire group.
    var isEnabled: Bool
    mutating func setEnabled(
        _ enabled: Bool
    ) {
        isEnabled = enabled

        for index in events.indices {
            events[index].isEnabled = enabled
        }
    }

    init(
        id: UUID = UUID(),
        displayName: String,
        events: [FilterEvent] = [],
        defaultAction: FilterAction = .skip,
        defaultSeverity: FilterSeverity = .strong,
        isEnabled: Bool = true
    ) {
        self.id = id
        self.displayName = displayName
        self.events = events
        self.defaultAction = defaultAction
        self.defaultSeverity = defaultSeverity
        self.isEnabled = isEnabled
    }

    // MARK: - Statistics

    var eventCount: Int {
        events.count
    }

    var enabledEventCount: Int {
        events.filter(\.isEnabled).count
    }

    var totalDuration: TimeInterval {
        events.reduce(0) { total, event in
            total + event.duration
        }
    }

    var enabledDuration: TimeInterval {
        events
            .filter(\.isEnabled)
            .reduce(0) { total, event in
                total + event.duration
            }
    }
    mutating func refreshEnabledState() {

        isEnabled = events.allSatisfy {
            $0.isEnabled
        }
    }
}
