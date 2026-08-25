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
