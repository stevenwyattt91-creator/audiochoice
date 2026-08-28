import CryptoKit
import Foundation

/// Where the listener last stopped reading, as a scroll anchor.
struct ReaderPosition: Codable, Equatable {
    var paragraphIndex: Int = 0
    /// Fraction of the way through that paragraph, so the anchor survives a text-size
    /// change that makes paragraphs taller or shorter.
    var paragraphFraction: Double = 0
}

/// On-device storage for reading editions.
///
/// The extracted text is cached because unzipping and decoding a novel takes hundreds of
/// milliseconds and a large transient allocation; doing it on every book open was a
/// visible stall on Android before it was cached.
///
/// The alignment is cached against a hash of the exact text it was built for. Alignment
/// ranges are offsets into that specific string, so serving them against different text
/// would mistime every highlight rather than fail visibly.
enum ReaderStore {
    private static let folder = "ReadingEditions"

    private static var directory: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent(folder, isDirectory: true)
    }

    private static func ensureDirectory() throws {
        var url = directory
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        // A listener's EPUB is their own file; there is no reason to inflate their iCloud
        // backup with a copy of it.
        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        try? url.setResourceValues(values)
    }

    // MARK: - The EPUB itself

    static func epubURL(bookID: UUID) -> URL {
        directory.appendingPathComponent("\(bookID.uuidString).epub")
    }

    static func hasEpub(bookID: UUID) -> Bool {
        FileManager.default.fileExists(atPath: epubURL(bookID: bookID).path)
    }

    /// Copies the picked file into private storage, because a document-picker URL is only
    /// valid for the length of the security-scoped access that produced it.
    static func saveEpub(from sourceURL: URL, bookID: UUID) throws {
        try ensureDirectory()
        let destination = epubURL(bookID: bookID)
        if FileManager.default.fileExists(atPath: destination.path) {
            try FileManager.default.removeItem(at: destination)
        }
        try FileManager.default.copyItem(at: sourceURL, to: destination)
    }

    static func removeEpub(bookID: UUID) {
        for url in [epubURL(bookID: bookID), textURL(bookID: bookID), alignmentURL(bookID: bookID)] {
            try? FileManager.default.removeItem(at: url)
        }
        UserDefaults.standard.removeObject(forKey: positionKey(bookID))
    }

    // MARK: - Extracted text

    private static func textURL(bookID: UUID) -> URL {
        directory.appendingPathComponent("\(bookID.uuidString).txt")
    }

    static func cachedText(bookID: UUID) -> String? {
        guard let data = try? Data(contentsOf: textURL(bookID: bookID)) else { return nil }
        return String(data: data, encoding: .utf8)
    }

    static func saveText(_ text: String, bookID: UUID) {
        try? ensureDirectory()
        try? Data(text.utf8).write(to: textURL(bookID: bookID), options: .atomic)
    }

    // MARK: - Cached alignment

    private struct CachedAlignment: Codable {
        let textHash: String
        let ranges: [ReaderTimingRange]
    }

    private static func alignmentURL(bookID: UUID) -> URL {
        directory.appendingPathComponent("\(bookID.uuidString).alignment.json")
    }

    /// Identifies the exact text an alignment was built for.
    static func textHash(_ text: String) -> String {
        SHA256.hash(data: Data(text.utf8)).map { String(format: "%02x", $0) }.joined()
    }

    /// Ranges previously stored for this book, but only when they were built for the same
    /// text. Different text means the offsets no longer mean anything.
    static func cachedAlignment(bookID: UUID, matching text: String) -> [ReaderTimingRange]? {
        guard let data = try? Data(contentsOf: alignmentURL(bookID: bookID)),
              let cached = try? JSONDecoder().decode(CachedAlignment.self, from: data),
              cached.textHash == textHash(text) else { return nil }
        return cached.ranges
    }

    static func saveAlignment(_ ranges: [ReaderTimingRange], bookID: UUID, text: String) {
        try? ensureDirectory()
        let payload = CachedAlignment(textHash: textHash(text), ranges: ranges)
        guard let data = try? JSONEncoder().encode(payload) else { return }
        try? data.write(to: alignmentURL(bookID: bookID), options: .atomic)
    }

    // MARK: - Reading position

    private static func positionKey(_ bookID: UUID) -> String {
        "readerPosition.\(bookID.uuidString)"
    }

    static func position(bookID: UUID) -> ReaderPosition {
        guard let data = UserDefaults.standard.data(forKey: positionKey(bookID)),
              let position = try? JSONDecoder().decode(ReaderPosition.self, from: data) else {
            return ReaderPosition()
        }
        return position
    }

    static func savePosition(_ position: ReaderPosition, bookID: UUID) {
        guard let data = try? JSONEncoder().encode(position) else { return }
        UserDefaults.standard.set(data, forKey: positionKey(bookID))
    }
}
