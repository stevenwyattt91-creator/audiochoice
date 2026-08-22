import Foundation

enum ConversionStatus: String, Codable {
    case notNeeded
    case waiting
    case converting
    case completed
    case failed
}
