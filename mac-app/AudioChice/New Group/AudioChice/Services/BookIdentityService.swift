import Foundation

final class BookIdentityService {

    func identify(
        title: String,
        author: String? = nil
    ) -> BookIdentity {

        var workingTitle = title
            .trimmingCharacters(in: .whitespacesAndNewlines)

        var editionType: EditionType = .standard
        var partNumber: Int?
        var totalParts: Int?
        var seriesTitle: String?
        var seriesNumber: Int?

        var confidence = 0.45

        // Detect dramatized or GraphicAudio-style productions.
        if contains(
            workingTitle,
            phrase: "dramatized adaptation"
        ) {
            editionType = .dramatizedAdaptation
            confidence += 0.15

            workingTitle = removingPhrase(
                "dramatized adaptation",
                from: workingTitle
            )
        }

        if contains(
            workingTitle,
            phrase: "graphic audio"
        ) || contains(
            workingTitle,
            phrase: "graphicaudio"
        ) {
            editionType = .graphicAudio
            confidence += 0.15

            workingTitle = removingPhrase(
                "graphic audio",
                from: workingTitle
            )

            workingTitle = removingPhrase(
                "graphicaudio",
                from: workingTitle
            )
        }

        if contains(
            workingTitle,
            phrase: "full cast"
        ) {
            if editionType == .standard {
                editionType = .fullCast
            }

            confidence += 0.10

            workingTitle = removingPhrase(
                "full cast",
                from: workingTitle
            )
        }

        if contains(
            workingTitle,
            phrase: "abridged"
        ) {
            editionType = .abridged
            confidence += 0.10

            workingTitle = removingPhrase(
                "abridged",
                from: workingTitle
            )
        }

        // Detect patterns such as:
        // Part 2 of 2
        if let partMatch = firstMatch(
            pattern: #"(?i)\bpart\s*(\d+)\s*of\s*(\d+)\b"#,
            in: workingTitle
        ) {
            partNumber = integerCapture(
                number: 1,
                from: partMatch,
                in: workingTitle
            )

            totalParts = integerCapture(
                number: 2,
                from: partMatch,
                in: workingTitle
            )

            workingTitle = replacingMatch(
                partMatch,
                in: workingTitle,
                with: ""
            )

            confidence += 0.10
        }

        // Detect series patterns such as:
        // The Empyrean, Book 2
        if let seriesMatch = firstMatch(
            pattern: #"(?i)(?:the\s+)?([^,:()]+?),?\s+book\s*(\d+)\b"#,
            in: workingTitle
        ) {
            let capturedSeries = stringCapture(
                number: 1,
                from: seriesMatch,
                in: workingTitle
            )

            seriesTitle = cleanSeriesTitle(capturedSeries)

            seriesNumber = integerCapture(
                number: 2,
                from: seriesMatch,
                in: workingTitle
            )

            workingTitle = replacingMatch(
                seriesMatch,
                in: workingTitle,
                with: ""
            )

            confidence += 0.10
        }

        workingTitle = cleanWorkTitle(workingTitle)

        if workingTitle.isEmpty {
            workingTitle = title
            confidence = 0.20
        }

        return BookIdentity(
            workTitle: workingTitle,
            seriesTitle: seriesTitle,
            seriesNumber: seriesNumber,
            editionType: editionType,
            partNumber: partNumber,
            totalParts: totalParts,
            confidence: min(confidence, 1.0)
        )
    }

    private func contains(
        _ text: String,
        phrase: String
    ) -> Bool {
        text.range(
            of: phrase,
            options: [.caseInsensitive, .diacriticInsensitive]
        ) != nil
    }

    private func removingPhrase(
        _ phrase: String,
        from text: String
    ) -> String {
        text.replacingOccurrences(
            of: phrase,
            with: "",
            options: [.caseInsensitive, .diacriticInsensitive]
        )
    }

    private func firstMatch(
        pattern: String,
        in text: String
    ) -> NSTextCheckingResult? {
        guard let regex = try? NSRegularExpression(
            pattern: pattern
        ) else {
            return nil
        }

        let fullRange = NSRange(
            text.startIndex..<text.endIndex,
            in: text
        )

        return regex.firstMatch(
            in: text,
            range: fullRange
        )
    }

    private func stringCapture(
        number: Int,
        from match: NSTextCheckingResult,
        in text: String
    ) -> String? {
        guard number < match.numberOfRanges else {
            return nil
        }

        let captureRange = match.range(at: number)

        guard captureRange.location != NSNotFound,
              let swiftRange = Range(
                captureRange,
                in: text
              ) else {
            return nil
        }

        return String(text[swiftRange])
            .trimmingCharacters(
                in: .whitespacesAndNewlines
            )
    }

    private func integerCapture(
        number: Int,
        from match: NSTextCheckingResult,
        in text: String
    ) -> Int? {
        guard let captured = stringCapture(
            number: number,
            from: match,
            in: text
        ) else {
            return nil
        }

        return Int(captured)
    }

    private func replacingMatch(
        _ match: NSTextCheckingResult,
        in text: String,
        with replacement: String
    ) -> String {
        guard let range = Range(
            match.range,
            in: text
        ) else {
            return text
        }

        var result = text
        result.replaceSubrange(
            range,
            with: replacement
        )

        return result
    }

    private func cleanSeriesTitle(
        _ value: String?
    ) -> String? {
        guard var value else {
            return nil
        }

        value = value.trimmingCharacters(
            in: CharacterSet(
                charactersIn: " ,:-()"
            )
        )

        if value.isEmpty {
            return nil
        }

        return value
    }

    private func cleanWorkTitle(
        _ value: String
    ) -> String {
        var result = value

        result = result.replacingOccurrences(
            of: "()",
            with: ""
        )

        result = result.replacingOccurrences(
            of: "  ",
            with: " "
        )

        result = result.trimmingCharacters(
            in: CharacterSet(
                charactersIn: " ,:-()"
            )
        )

        while result.contains("  ") {
            result = result.replacingOccurrences(
                of: "  ",
                with: " "
            )
        }

        return result
    }
}
