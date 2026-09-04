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
        // A retail product identifier names one published recording outright, so two copies
        // sharing one are the same entry however their titles are spelled. Consulted first
        // because it is the only signal here that cannot be mistaken.
        var indexByIdentifier: [String: Int] = [:]
        var indexByKey: [String: [Int]] = [:]

        for book in books {
            let identifier = book.productIdentifier
                .map { $0.uppercased() }
                .flatMap { $0.isEmpty ? nil : $0 }
            if let identifier, let known = indexByIdentifier[identifier] {
                clusters[known].append(book)
                continue
            }

            let key = groupKey(book)
            var placed: Int?
            for index in indexByKey[key] ?? [] where isSameBook(clusters[index][0], book) {
                clusters[index].append(book)
                placed = index
                break
            }

            // Same author, same part, same runtime to the second: one recording whose title
            // is written two ways. Only reached when the titles disagree, since matching
            // titles would have been caught above.
            if placed == nil {
                for index in clusters.indices where isSameRecording(clusters[index][0], book) {
                    clusters[index].append(book)
                    placed = index
                    break
                }
            }

            if placed == nil {
                clusters.append([book])
                placed = clusters.count - 1
                indexByKey[key, default: []].append(clusters.count - 1)
            }

            // Remember the identifier against whichever cluster this joined, so a later copy
            // carrying the same one lands in the same place.
            if let identifier, let placed, indexByIdentifier[identifier] == nil {
                indexByIdentifier[identifier] = placed
            }
        }

        // Order is preserved from the incoming list, which the server already sorted by
        // title, so cleaning up does not reshuffle the catalogue.
        return clusters.map(best)
    }

    /// Floor for how far two runtimes may differ, used for a short book where a percentage
    /// of the runtime would be too tight to absorb ordinary rounding.
    private static let minimumRuntimeMatchSeconds: Double = 5

    /// How far two runtimes may differ, as a fraction of the longer one, and still be
    /// judged the same recording.
    ///
    /// Proportional rather than a fixed number of seconds, matching what re-encoding drift
    /// measured off the real catalogue actually looks like: it compounds across a longer
    /// file rather than adding a constant. A flat two seconds -- absolute, not proportional
    /// -- left real re-encoded copies of Fourth Wing, Funny Story and The Deal listed twice
    /// even after their titles had already been unified server-side. 0.2% comfortably covers
    /// the largest drift seen (0.065%) with room to spare, while still rejecting an abridged
    /// reading against an unabridged one, which differs by double digits of percent.
    private static let maximumRuntimeDriftFraction: Double = 0.002

    /// Whether two entries are the same recording judged on runtime rather than title.
    ///
    /// The last resort for copies carrying no product identifier, where one was tagged by
    /// hand and the other named from a filename. Runtime discriminates because two different
    /// recordings of a book run to different lengths: a different narrator reads at a
    /// different pace.
    ///
    /// An author is required on both sides. Without one this would merge on runtime alone,
    /// and two unrelated books that happen to run the same length are not the same book.
    static func isSameRecording(_ left: ExploreCatalogBook, _ right: ExploreCatalogBook) -> Bool {
        // Two identifiers that disagree are evidence of two different recordings, and that
        // outranks a runtime coincidence. Equal identifiers never reach here, having already
        // merged above, so anything left with one on both sides is genuinely two books.
        if left.productIdentifier != nil && right.productIdentifier != nil { return false }
        guard let leftDuration = left.duration, leftDuration > 0,
              let rightDuration = right.duration, rightDuration > 0,
              runtimesAgree(leftDuration, rightDuration),
              partMarker(left.title) == partMarker(right.title),
              normalized(left.editionType) == normalized(right.editionType) else { return false }
        let leftAuthor = normalized(left.author)
        return !leftAuthor.isEmpty && leftAuthor == normalized(right.author)
    }

    /// Whether two runtimes are close enough to be the same recording.
    ///
    /// An unknown runtime is treated as agreement rather than as a mismatch, because an
    /// untagged file is one of the differences being reconciled here.
    private static func runtimesAgree(_ left: Double?, _ right: Double?) -> Bool {
        guard let left, left > 0, let right, right > 0 else { return true }
        let drift = abs(left - right)
        return drift <= minimumRuntimeMatchSeconds
            || drift <= max(left, right) * maximumRuntimeDriftFraction
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
              normalized(left.editionType) == normalized(right.editionType),
              // Two runtimes that disagree are two different readings of one book, whatever
              // the edition type says: a different narrator, or an abridgement. Merging them
              // would show one entry's scan for audio it does not describe.
              runtimesAgree(left.duration, right.duration) else { return false }

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
