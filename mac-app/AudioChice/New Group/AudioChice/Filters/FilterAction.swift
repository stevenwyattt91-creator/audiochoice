import Foundation

enum FilterAction: String, Codable, CaseIterable {

    case none
    case mute
    case skip

    var displayName: String {
        switch self {

        case .none:
            return "Allow"

        case .mute:
            return "Mute"

        case .skip:
            return "Skip"
        }
    }
}
