import Foundation

struct BookIdentity: Codable, Equatable {
    var workTitle: String
    var seriesTitle: String?
    var seriesNumber: Int?

    var editionType: EditionType

    var partNumber: Int?
    var totalParts: Int?

    var confidence: Double
}

enum EditionType: String, Codable {
    case standard
    case dramatizedAdaptation
    case graphicAudio
    case fullCast
    case abridged
    case unknown

    var displayName: String {
        switch self {
        case .standard:
            return "Standard Audiobook"

        case .dramatizedAdaptation:
            return "Dramatized Adaptation"

        case .graphicAudio:
            return "GraphicAudio"

        case .fullCast:
            return "Full Cast"

        case .abridged:
            return "Abridged"

        case .unknown:
            return "Unknown Edition"
        }
    }
}
