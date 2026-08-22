import Foundation

enum ScanStatus: String, Codable {
    case notScanned
    case scanning
    case completed
    case failed
}
