import Foundation

/// Turns a book's enabled filter events into character ranges to remove from the text.
///
/// Two independent mechanisms, because either alone leaves a hole:
///
/// 1. Timing overlap. Where a filtered audio range overlaps an aligned text range, the
///    corresponding characters are removed. Only genuine overlaps are used — falling back
///    to a nearby section could hide unrelated text, which is worse than leaving a small
///    transcript gap visible.
/// 2. Word matching. Profanity can be hidden directly from the EPUB, which stays dependable
///    even where the audiobook-to-ebook timing has a gap.
enum ReaderFilterMasks {
    /// @param events the events already filtered to the listener's enabled categories.
    static func build(
        events: [ScanEvent],
        timings: [ReaderTimingRange],
        text: String
    ) -> [ReaderMask] {
        var masks: [ReaderMask] = []
        masks.append(contentsOf: timingMasks(events: events, timings: timings))
        masks.append(contentsOf: wordMasks(events: events, text: text))
        return masks.merged()
    }

    private static func timingMasks(
        events: [ScanEvent],
        timings: [ReaderTimingRange]
    ) -> [ReaderMask] {
        var masks: [ReaderMask] = []
        for event in events {
            for timing in timings {
                let overlapStart = max(event.startTime, timing.startTime)
                let overlapEnd = min(event.endTime, timing.endTime)
                guard overlapEnd > overlapStart else { continue }

                let clampedStart = min(max(overlapStart, timing.startTime), timing.endTime)
                let clampedEnd = min(max(overlapEnd, timing.startTime), timing.endTime)
                let duration = max(timing.endTime - timing.startTime, 0.001)
                let length = Double(timing.endCharacter - timing.startCharacter)
                let rawStart = timing.startCharacter
                    + Int(length * ((clampedStart - timing.startTime) / duration))
                let rawEnd = timing.startCharacter
                    + Int(length * ((clampedEnd - timing.startTime) / duration))
                let start = min(rawStart, rawEnd)
                let end = max(max(rawStart, rawEnd), start + 1)
                if end > start { masks.append(ReaderMask(start: start, end: end)) }
            }
        }
        return masks
    }

    private static func wordMasks(events: [ScanEvent], text: String) -> [ReaderMask] {
        let words = Set(
            events.compactMap { $0.aggregateDisplay?.trimmingCharacters(in: .whitespaces) }
                .filter { $0.count >= 2 && $0.count <= 64 }
                .filter { $0.caseInsensitiveCompare("Censored word") != .orderedSame }
        )
        guard !words.isEmpty else { return [] }

        var masks: [ReaderMask] = []
        for word in words {
            // The server deliberately sends displays such as "f**k", never the uncensored
            // word. Each asterisk stands for one letter, so searching for a literal
            // asterisk would make word filters a no-op.
            let pattern = word.map { character -> String in
                character == "*" ? "[\\p{L}]" : NSRegularExpression.escapedPattern(for: String(character))
            }.joined()
            guard let expression = try? NSRegularExpression(
                pattern: "\\b\(pattern)\\b",
                options: [.caseInsensitive]
            ) else { continue }

            // NSRegularExpression ranges are already in UTF-16 units, which is the same
            // coordinate space the alignment uses.
            for match in expression.matches(in: text, range: NSRange(text.startIndex..., in: text)) {
                let range = match.range
                guard range.location != NSNotFound, range.length > 0 else { continue }
                masks.append(ReaderMask(start: range.location, end: range.location + range.length))
            }
        }
        return masks
    }
}
