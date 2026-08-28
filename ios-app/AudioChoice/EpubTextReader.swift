import Foundation

/// Extracts a book's plain text from an EPUB, in reading order.
///
/// Reads the OPF spine rather than ZIP-entry order, which is frequently not book order.
/// A direct port of the Android reader so both platforms present the same text.
///
/// The output string is the coordinate space everything else uses: the server's reader
/// alignment returns character ranges into exactly this string, and the paragraph parser
/// only ever indexes it. Changing how this joins or normalises text invalidates every
/// cached alignment, so it should be treated as a stable format rather than an
/// implementation detail.
enum EpubTextReader {
    /// Unzips and decodes the whole book, so it must stay off the main thread.
    static func read(fileURL: URL) async -> String {
        await Task.detached(priority: .userInitiated) { readBlocking(fileURL: fileURL) }.value
    }

    static func readBlocking(fileURL: URL) -> String {
        guard let archive = ZipArchive(fileURL: fileURL) else { return "" }

        var documents: [String: String] = [:]
        for entry in archive.entries where !entry.name.hasSuffix("/") {
            guard let data = archive.contents(of: entry) else { continue }
            // Some producers emit UTF-8 without a BOM and others latin-1; falling back
            // keeps a mis-encoded chapter from dropping out of the book entirely.
            let text = String(data: data, encoding: .utf8)
                ?? String(data: data, encoding: .isoLatin1)
            guard let text else { continue }
            documents[normalize(entry.name)] = text
        }

        let container = documents["meta-inf/container.xml"] ?? ""
        let packagePath = firstMatch(Self.rootfilePattern, in: container).map(normalize)
        let package = packagePath.flatMap { documents[$0] } ?? ""
        let basePath = packagePath.map { path -> String in
            guard let separator = path.lastIndex(of: "/") else { return "" }
            return String(path[path.startIndex..<separator])
        } ?? ""

        var manifest: [String: String] = [:]
        for match in matches(Self.manifestItemPattern, in: package) where match.count >= 3 {
            manifest[match[1]] = resolve(base: basePath, href: match[2])
        }
        let spine = matches(Self.spineItemPattern, in: package)
            .compactMap { $0.count >= 2 ? manifest[$0[1]] : nil }

        let pages: [String]
        if !spine.isEmpty {
            pages = spine.compactMap { documents[$0] }
        } else {
            // No usable spine, so fall back to every document in name order, which is at
            // least deterministic.
            pages = documents.keys
                .filter { $0.hasSuffix(".xhtml") || $0.hasSuffix(".html") || $0.hasSuffix(".htm") }
                .sorted()
                .compactMap { documents[$0] }
        }

        return trimmingFrontMatter(pages.map(htmlToText))
            .joined(separator: "\n\n")
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private static func htmlToText(_ html: String) -> String {
        var text = html
        text = replacing(#"(?is)<script.*?</script>|<style.*?</style>"#, in: text, with: "")
        // One newline per block element, which is what makes a newline run the paragraph
        // separator the parser looks for.
        text = replacing(#"(?i)<br\s*/?>|</p>|</div>|</h[1-6]>|</li>"#, in: text, with: "\n")
        text = replacing(#"<[^>]+>"#, in: text, with: "")
        text = text
            .replacingOccurrences(of: "&nbsp;", with: " ")
            .replacingOccurrences(of: "&amp;", with: "&")
            .replacingOccurrences(of: "&quot;", with: "\"")
            .replacingOccurrences(of: "&apos;", with: "'")
        text = replacing(#"[ \t]+"#, in: text, with: " ")
        text = replacing(#"\n{3,}"#, in: text, with: "\n\n")
        return text.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private static func resolve(base: String, href: String) -> String {
        let joined = base.isEmpty ? href : "\(base)/\(href)"
        return normalize(joined.replacingOccurrences(of: "../", with: ""))
    }

    private static func normalize(_ path: String) -> String {
        path.replacingOccurrences(of: "\\", with: "/").lowercased()
    }

    /// Audiobooks normally open on the story rather than on copyright and contents pages,
    /// so dropping the leading matter keeps the text and the audio roughly in step.
    private static func trimmingFrontMatter(_ pages: [String]) -> [String] {
        let firstStoryPage = pages.firstIndex { page in
            let opening = String(page.prefix(600))
            return opening.range(of: Self.storyStartPattern, options: .regularExpression) != nil
        }
        guard let firstStoryPage, firstStoryPage > 0 else { return pages }
        return Array(pages[firstStoryPage...])
    }

    // MARK: - Regular expressions

    private static let rootfilePattern = #"(?i)<rootfile[^>]*full-path=["']([^"']+)"#
    private static let manifestItemPattern =
        #"(?i)<item\b(?=[^>]*\bid=["']([^"']+))(?=[^>]*\bhref=["']([^"']+))[^>]*>"#
    private static let spineItemPattern = #"(?i)<itemref[^>]*\bidref=["']([^"']+)"#
    private static let storyStartPattern =
        #"(?im)^\s*(prologue|chapter\s+(one|1)\b|part\s+(one|1)\b)"#

    private static func replacing(_ pattern: String, in value: String, with replacement: String) -> String {
        value.replacingOccurrences(of: pattern, with: replacement, options: .regularExpression)
    }

    private static func firstMatch(_ pattern: String, in value: String) -> String? {
        matches(pattern, in: value).first.flatMap { $0.count >= 2 ? $0[1] : nil }
    }

    /// All matches, each as its full match followed by its capture groups.
    private static func matches(_ pattern: String, in value: String) -> [[String]] {
        guard let expression = try? NSRegularExpression(pattern: pattern) else { return [] }
        let range = NSRange(value.startIndex..., in: value)
        return expression.matches(in: value, range: range).map { match in
            (0..<match.numberOfRanges).map { index in
                guard let groupRange = Range(match.range(at: index), in: value) else { return "" }
                return String(value[groupRange])
            }
        }
    }
}
