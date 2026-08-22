import Foundation

struct BookMetadata {
    var title: String?
    var author: String?
    var narrator: String?
    var duration: TimeInterval?
    var coverArtData: Data?
    var chapters: [Chapter]
}
