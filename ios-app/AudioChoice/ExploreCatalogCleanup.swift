import Foundation

/// Collapses the repeated entries the explore catalogue accumulates.
///
/// The same recording arrives from different listeners' files, and their tags disagree:
/// one has an author, another has none; one title carries "(Unabridged)", a series prefix
/// or a bracketed edition note. The server groups on normalised title plus author, so any
/// of those differences produces a second row for a book that is already listed.
///
/// Two things are deliberately *not* merged. A book split across parts stays split,
/// because those are genuinely different audio. So do different edition types, because a
/// dramatised recording and a straight reading have different runtimes and different
/// filter data, and showing one in place of the other would apply the wrong scan.
enum ExploreCatalogCleanup {

    static func deduplicated(_ books: [ExploreCatalogBook]) -> [ExploreCatalogBook] {
        var clusters: [[ExploreCatalogBook]] = []
        var indexByKey: [String: [Int]] = [:]

        for book in books {
            let key = groupKey(book)
            var placed = false
            for index in indexByKey[key] ?? [] where isSameBook(clusters[index][0], book) {
                clusters[index].append(book)
                placed = true
                break
            }
            if !placed {
                clusters.append([book])
                indexByKey[key, default: []].append(clusters.count - 1)
            }
        }

        // Order is preserved from the incoming list, which the server already sorted by
        // title, so cleaning up does not reshuffle the catalogue.
        return clusters.map(best)
    }

    /// The entry to keep when several describe the same recording.
    ///
    /// A cover first, because an entry without one is the visible symptom being cleaned up.
    /// Then the richest scan, then the entry that can best describe itself on the card.
    static func best(_ candidates: [ExploreCatalogBook]) -> ExploreCatalogBook {
        candidates.dropFirst().reduce(candidates[0]) { current, candidate in
            isBetter(candidate, than: current) ? candidate : current
        }
    }

    private static func isBetter(
        _ candidate: ExploreCatalogBook,
        than current: ExploreCatalogBook
    ) -> Bool {
        let candidateHasCover = hasCover(candidate)
        if candidateHasCover != hasCover(current) { return candidateHasCover }
        if candidate.eventCount != current.eventCount {
            return candidate.eventCount > current.eventCount
        }
        let candidateScore = metadataScore(candidate)
        let currentScore = metadataScore(current)
        if candidateScore != currentScore { return candidateScore > currentScore }
        // Everything else being equal, pick by identifier rather than by arrival order, so
        // reloading the catalogue cannot swap which of two equals is shown.
        return candidate.catalogID < current.catalogID
    }

    private static func hasCover(_ book: ExploreCatalogBook) -> Bool {
        !(book.coverImageURL ?? "").isEmpty
    }

    /// Prefers the entry that can actually describe itself on the card.
    private static func metadataScore(_ book: ExploreCatalogBook) -> Int {
        var score = 0
        if !(book.author ?? "").isEmpty { score += 1 }
        if !(book.description ?? "").isEmpty { score += 1 }
        if (book.duration ?? 0) > 0 { score += 1 }
        return score
    }

    /// Cheap bucket key. Deliberately excludes the author, because a missing author is one
    /// of the differences being reconciled; `isSameBook` decides the real matches.
    static func groupKey(_ book: ExploreCatalogBook) -> String {
        "\(normalizedTitle(book.title))|\(partMarker(book.title) ?? "")|\(normalized(book.editionType))"
    }

    static func isSameBook(_ left: ExploreCatalogBook, _ right: ExploreCatalogBook) -> Bool {
        guard normalizedTitle(left.title) == normalizedTitle(right.title),
              partMarker(left.title) == partMarker(right.title),
              normalized(left.editionType) == normalized(right.editionType) else { return false }

        let leftAuthor = normalized(left.author)
        let rightAuthor = normalized(right.author)
        // An absent author is treated as agreement rather than as a distinct book: it is
        // an untagged file, not a different recording. Two *different* authors keep their
        // own entries, since a shared title is not evidence of a shared book.
        return leftAuthor.isEmpty || rightAuthor.isEmpty || leftAuthor == rightAuthor
    }

    /// "Part 2 of 3", however it was written, or nil.
    ///
    /// Extracted before the title is normalised so that parts of one release stay separate
    /// while everything else about them is allowed to differ.
    static func partMarker(_ title: String) -> String? {
        let pattern = #"(?:part|disc|volume|vol|book)?\s*(\d+)\s*(?:of|/)\s*(\d+)"#
        guard let expression = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive]),
              let match = expression.firstMatch(
                in: title, range: NSRange(title.startIndex..., in: title)
              ),
              let first = Range(match.range(at: 1), in: title),
              let second = Range(match.range(at: 2), in: title)
        else { return nil }
        return "\(title[first]) of \(title[second])"
    }

    static func normalizedTitle(_ title: String) -> String {
        var value = title

        // Anything parenthesised or bracketed: "(Unabridged)", "[Dramatised]", and the
        // rest of the noise that differs between one listener's tags and another's.
        for pattern in [#"\([^)]*\)"#, #"\[[^\]]*\]"#, #"\{[^}]*\}"#] {
            value = value.replacingOccurrences(
                of: pattern, with: " ", options: .regularExpression
            )
        }

        value = value.replacingOccurrences(
            of: #"(?:part|disc|volume|vol|book)?\s*\d+\s*(?:of|/)\s*\d+"#,
            with: " ",
            options: [.regularExpression, .caseInsensitive]
        )

        // Trailing edition wording, and the separator before it. Anchored to the end so a
        // title that genuinely contains one of these words keeps it.
        let suffixes = [
            "unabridged", "abridged", "audiobook", "audio book", "audio edition",
            "a full cast production", "full cast production", "dramatized adaptation",
            "dramatised adaptation", "graphic audio", "graphicaudio"
        ]
        var trimmedSomething = true
        while trimmedSomething {
            trimmedSomething = false
            let simplified = normalized(value)
            for suffix in suffixes where simplified.hasSuffix(" \(suffix)") {
                value = String(simplified.dropLast(suffix.count + 1))
                trimmedSomething = true
                break
            }
        }

        return normalized(value)
    }

    /// Lowercased, with everything that is not a letter or digit reduced to single spaces,
    /// so punctuation and separator differences stop mattering.
    private static func normalized(_ value: String?) -> String {
        (value ?? "")
            .replacingOccurrences(of: "[^a-z0-9]+", with: " ", options: [.regularExpression, .caseInsensitive])
            .trimmingCharacters(in: .whitespaces)
            .lowercased()
    }
}
