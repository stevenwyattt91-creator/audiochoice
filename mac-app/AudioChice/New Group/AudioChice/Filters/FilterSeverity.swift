import Foundation

enum FilterSeverity: String, Codable, CaseIterable {

    case minimal
    case mild
    case moderate
    case strong
    case explicit

    var displayName: String {
        switch self {

        case .minimal:
            return "Minimal"

        case .mild:
            return "Mild"

        case .moderate:
            return "Moderate"

        case .strong:
            return "Strong"

        case .explicit:
            return "Explicit"
        }
    }
}
