import Foundation

struct FilterEvent: Identifiable, Codable {

    let id: UUID

    /// Short title shown in the UI.
    var title: String

    /// Safe, non-graphic description shown to users.
    var displaySummary: String

    /// Start of the filtered content.
    var startTime: TimeInterval

    /// End of the filtered content.
    var endTime: TimeInterval

    /// What AudioChoice should do.
    var action: FilterAction

    /// How severe this event is.
    var severity: FilterSeverity

    /// Whether this event is currently enabled.
    var isEnabled: Bool

    init(
        id: UUID = UUID(),
        title: String,
        displaySummary: String,
        startTime: TimeInterval,
        endTime: TimeInterval,
        action: FilterAction = .skip,
        severity: FilterSeverity = .strong,
        isEnabled: Bool = true
    ) {
        self.id = id
        self.title = title
        self.displaySummary = displaySummary
        self.startTime = startTime
        self.endTime = endTime
        self.action = action
        self.severity = severity
        self.isEnabled = isEnabled
    }

    var duration: TimeInterval {
        max(endTime - startTime, 0)
    }
}
