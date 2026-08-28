import Foundation

enum AudiobookTitleFormatter {
    static func format(
        _ rawValue: String,
        editionType: String? = nil,
        partNumber: Int? = nil,
        totalParts: Int? = nil
    ) -> String {
        var title = rawValue.trimmingCharacters(in: .whitespacesAndNewlines)
        title = replacing(#"\s*\(\d+\)\s*$"#, in: title, with: "")

        let embeddedPart = firstMatch(#"(?i)\bpart\s*(\d+)\s*(?:of|/)\s*(\d+)\b"#, in: title)
        let part = positive(partNumber) ?? embeddedPart.flatMap { Int($0[1]) }
        let total = positive(totalParts) ?? embeddedPart.flatMap { Int($0[2]) }
        let combinedEdition = [title, editionType ?? ""].joined(separator: " ")
        let dramatized = combinedEdition.range(
            of: #"(?i)\b(dramati[sz]ed(?: adaptation)?|graphic\s*audio|full\s*cast)\b"#,
            options: .regularExpression
        ) != nil

        title = replacing(#"(?i)\s*\(?\s*,?\s*part\s*\d+\s*(?:of|/)\s*\d+\s*\)?"#, in: title, with: "")
        title = replacing(#"(?i)\s*\((?:dramati[sz]ed(?: adaptation)?|graphic\s*audio|full\s*cast)\)"#, in: title, with: "")
        title = replacing(#"(?i)\s+[-–—:]?\s*(?:dramati[sz]ed(?: adaptation)?|graphic\s*audio|full\s*cast)\s*$"#, in: title, with: "")
        title = replacing(#"\s+"#, in: title, with: " ").trimmingCharacters(in: .whitespacesAndNewlines)
        if title.isEmpty { title = "Untitled Audiobook" }

        if let part, let total { title += " (Part \(part) of \(total))" }
        if dramatized { title += " (Dramatized)" }
        return title
    }

    /// Tidies a filename being used as a title of last resort.
    ///
    /// Applied only when a file carries no title tag, so a real title is never
    /// rewritten. It cannot fix a misspelling — only tag or catalog matching can do
    /// that — but it stops "fourth wingggg (3112r)" reaching the library verbatim.
    ///
    /// Bracketed groups naming a part, format or edition are kept, because edition
    /// matching reads the part out of the title and losing it would merge two halves
    /// of one audiobook.
    static func cleanFilename(_ rawValue: String) -> String {
        var text = rawValue.replacingOccurrences(of: "_", with: " ")
        // Dots separate words in "the.hobbit.unabridged" but are punctuation in
        // "Vol. 2", so only expand them when there are no real spaces at all.
        if !text.contains(" ") { text = text.replacingOccurrences(of: ".", with: " ") }
        text = removingNoiseGroups(text)
        text = replacing(#"^\s*\d{1,3}\s*[-–—.)]\s*"#, in: text, with: "")
        text = replacing(#"^[\s\-–—_.]+"#, in: text, with: "")
        text = replacing(#"[\s\-–—_.]+$"#, in: text, with: "")
        text = replacing(#"\s{2,}"#, in: text, with: " ").trimmingCharacters(in: .whitespacesAndNewlines)
        return text
    }

    private static let meaningfulTokens: Set<String> = [
        "part", "parts", "of", "book", "vol", "volume", "disc", "cd",
        "unabridged", "abridged", "dramatized", "dramatised", "dramatization",
        "complete", "collection", "edition", "box", "boxed", "set", "narrated",
    ]

    private static let technicalTokens: Set<String> = [
        "kbps", "kbit", "khz", "hz", "bit", "bits", "kb", "mb", "gb",
        "mp3", "m4a", "m4b", "mp4", "aax", "aaxc", "aac", "flac", "ogg", "opus", "wav", "wma",
        "stereo", "mono", "vbr", "cbr", "abr", "audiobook", "audio", "rip", "retail",
    ]

    private static func removingNoiseGroups(_ text: String) -> String {
        guard let expression = try? NSRegularExpression(pattern: #"[(\[{]([^)\]}]*)[)\]}]"#) else { return text }
        var result = text
        // Replace from the end so earlier ranges stay valid.
        let matches = expression.matches(in: text, range: NSRange(text.startIndex..., in: text)).reversed()
        for match in matches {
            guard let whole = Range(match.range, in: result),
                  let inner = Range(match.range(at: 1), in: result) else { continue }
            let contents = String(result[inner])
            if keepsGroup(contents) { continue }
            result.replaceSubrange(whole, with: "")
        }
        return result
    }

    private static func keepsGroup(_ contents: String) -> Bool {
        let tokens = contents.lowercased()
            .split(whereSeparator: { !$0.isLetter && !$0.isNumber })
            .map(String.init)
        if tokens.isEmpty { return false }
        if tokens.contains(where: meaningfulTokens.contains) { return true }
        // Compact markers like "1of2" or "Pt.2" tokenize as one letters-and-digits run,
        // the same shape as a junk code. Part information wins that tie.
        if contents.range(
            of: #"(?i)(\d+\s*of\s*\d+|(?:part|pt|cd|disc|vol|volume|book)\.?\s*\d+)"#,
            options: .regularExpression
        ) != nil { return true }
        if tokens.contains(where: technicalTokens.contains) { return false }
        // Codes such as "3112r" or an ASIN mix letters and digits.
        if tokens.contains(where: { token in
            token.contains(where: \.isNumber) && token.contains(where: \.isLetter)
        }) { return false }
        // A bare number is a duplicate-download marker, not a title.
        if tokens.allSatisfy({ $0.allSatisfy(\.isNumber) }) { return false }
        // Real words simply absent from the vocabulary above are safer kept.
        return true
    }

    static func comparisonKey(_ value: String) -> String {
        String(format(value)
            .folding(options: [.caseInsensitive, .diacriticInsensitive], locale: .current)
            .filter { $0.isLetter || $0.isNumber })
    }

    static func matches(_ lhs: String, _ rhs: String) -> Bool {
        comparisonKey(lhs) == comparisonKey(rhs)
    }

    private static func positive(_ value: Int?) -> Int? {
        guard let value, value > 0 else { return nil }
        return value
    }

    private static func replacing(_ pattern: String, in value: String, with replacement: String) -> String {
        value.replacingOccurrences(of: pattern, with: replacement, options: .regularExpression)
    }

    private static func firstMatch(_ pattern: String, in value: String) -> [String]? {
        guard let expression = try? NSRegularExpression(pattern: pattern),
              let match = expression.firstMatch(in: value, range: NSRange(value.startIndex..., in: value)) else { return nil }
        return (0..<match.numberOfRanges).compactMap { index in
            guard let range = Range(match.range(at: index), in: value) else { return nil }
            return String(value[range])
        }
    }
}
