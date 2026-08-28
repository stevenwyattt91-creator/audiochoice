import Foundation

/// One switch the listener can independently turn on or off.
///
/// Repeated profanity keeps every timestamp for playback but presents as a single
/// control, because a list with one row per utterance of the same word is not
/// something anyone can reasonably work through.
struct PlaybackFilterControl: Identifiable, Equatable {
    let key: String
    let label: String
    let occurrences: Int
    /// Nil for aggregate controls, which span the whole book rather than one moment.
    let startTime: Double?
    let isAggregate: Bool

    var id: String { key }
}

/// A taxonomy subgroup, such as "Strong profanity" inside Profanity.
struct PlaybackFilterGroup: Identifiable, Equatable {
    /// Lowercased group GUID.
    let id: String
    let label: String
    /// Lowercased category GUID, needed to roll a group up to its parent switch.
    let categoryID: String
    let controls: [PlaybackFilterControl]
}

/// A top-level content category, such as Profanity.
struct PlaybackFilterCategory: Identifiable, Equatable {
    /// Lowercased category GUID.
    let id: String
    let label: String
    let icon: String
    let groups: [PlaybackFilterGroup]

    var controlCount: Int { groups.reduce(0) { $0 + $1.controls.count } }
}

/// Builds the filter hierarchy a book actually needs, from the events its scan found.
///
/// A direct port of the Android client's PlaybackFilterTaxonomy. Keeping the two
/// identical matters because the same account can open the same book on either
/// platform, and the disabled keys saved by one are read by the other: a control
/// that is keyed differently across platforms would silently drop a listener's
/// choice rather than fail visibly.
enum PlaybackFilterTaxonomy {
    private struct Definition {
        let parentLabel: String
        let childLabel: String
    }

    /// The scanner composes group GUIDs from a category digit and a group index.
    private static func groupID(category: Int, group: Int) -> String {
        "\(category)1000000-0000-0000-0000-" + String(format: "%012d", group)
    }

    private static let definitions: [String: Definition] = {
        var values: [String: Definition] = [:]

        func add(_ category: Int, _ parentLabel: String, _ childLabels: [String]) {
            for (index, childLabel) in childLabels.enumerated() {
                values[groupID(category: category, group: index + 1)] =
                    Definition(parentLabel: parentLabel, childLabel: childLabel)
            }
        }

        add(1, "Sexual Content", [
            "Suggestive dialogue", "Sexual references", "Nudity",
            "Implied sexual activity", "Explicit sexual activity", "Complete sex scenes"
        ])
        add(2, "Profanity", [
            "Mild profanity", "Strong profanity", "Sexual profanity", "Slurs / derogatory language"
        ])
        // Violence is deliberately not a contiguous run. The scanner also emits lower
        // severity violence groups, and those are absent here so that neither the screen
        // nor playback treats an ordinary scuffle as something to remove. Anything with
        // no definition is dropped from the hierarchy, which is what keeps the visible
        // controls and the enforced events describing the same set.
        values[groupID(category: 3, group: 3)] = Definition(parentLabel: "Violence", childLabel: "Graphic violence / gore")
        values[groupID(category: 3, group: 4)] = Definition(parentLabel: "Violence", childLabel: "Torture")
        values[groupID(category: 3, group: 6)] = Definition(parentLabel: "Violence", childLabel: "Violence involving children")
        values[groupID(category: 3, group: 7)] = Definition(parentLabel: "Violence", childLabel: "Violence involving animals")
        add(4, "Drugs & Alcohol", [
            "Alcohol use", "Intoxication", "Drug references", "Drug use", "Drug abuse / overdose"
        ])
        add(5, "Blasphemy", ["Religious profanity", "Blasphemous statements"])
        add(6, "Self-Harm & Suicide", [
            "Self-harm references", "Suicidal thoughts", "Suicide attempt",
            "Depiction of self-harm / suicide"
        ])
        return values
    }()

    private static let icons: [String: String] = [
        "Sexual Content": "heart.slash",
        "Profanity": "text.badge.checkmark",
        "Violence": "shield",
        "Drugs & Alcohol": "pills",
        "Blasphemy": "quote.bubble",
        "Self-Harm & Suicide": "cross.case"
    ]

    /// Whether playback removes this event at all, before the listener's choices apply.
    ///
    /// An event outside the definitions above has no switch anywhere in the app, so
    /// removing it would take content away with nothing to turn it back on.
    static func isFilterable(_ event: ScanEvent) -> Bool {
        definitions[event.groupID.uuidString.lowercased()] != nil
    }

    /// The categories, groups and controls present in this book, whether or not the
    /// listener has since switched any of them off.
    ///
    /// Disabled controls stay in the list on purpose: dropping them would make a
    /// switch vanish the moment it was turned off, leaving no way to turn it back on.
    static func available(_ events: [ScanEvent]) -> [PlaybackFilterCategory] {
        // One aggregate can span several taxonomy subgroups. Show it once, in the first
        // subgroup it appears in chronologically, while playback keeps every range.
        var preferredGroupByKey: [String: String] = [:]
        for event in events.sorted(by: { $0.startTime < $1.startTime }) {
            guard let key = event.aggregateKey, !key.isEmpty else { continue }
            let group = event.groupID.uuidString.lowercased()
            if preferredGroupByKey[key] == nil { preferredGroupByKey[key] = group }
        }

        let visible = events.filter { event in
            guard let key = event.aggregateKey, !key.isEmpty else { return true }
            return preferredGroupByKey[key] == event.groupID.uuidString.lowercased()
        }

        let groups: [PlaybackFilterGroup] = Dictionary(grouping: visible) {
            $0.groupID.uuidString.lowercased()
        }.compactMap { groupID, groupEvents in
            guard let definition = definitions[groupID] else { return nil }

            let aggregates = Dictionary(
                grouping: groupEvents.filter { ($0.aggregateKey ?? "").isEmpty == false }
            ) { $0.aggregateKey! }
                .map { key, values in
                    PlaybackFilterControl(
                        key: key,
                        label: values.first?.aggregateDisplay ?? "Censored word",
                        occurrences: values.count,
                        startTime: nil,
                        isAggregate: true
                    )
                }

            let individuals = groupEvents
                .filter { ($0.aggregateKey ?? "").isEmpty }
                .map { event in
                    PlaybackFilterControl(
                        key: BookFilterSettings.eventKey(for: event),
                        label: event.safeDescription ?? definition.childLabel,
                        occurrences: 1,
                        startTime: event.startTime,
                        isAggregate: false
                    )
                }

            let controls = (aggregates + individuals).sorted { first, second in
                let firstStart = first.startTime ?? .greatestFiniteMagnitude
                let secondStart = second.startTime ?? .greatestFiniteMagnitude
                if firstStart != secondStart { return firstStart < secondStart }
                return first.label < second.label
            }

            return PlaybackFilterGroup(
                id: groupID,
                label: definition.childLabel,
                categoryID: (groupEvents.first?.categoryID.uuidString ?? "").lowercased(),
                controls: controls
            )
        }

        return Dictionary(grouping: groups) { definitions[$0.id]?.parentLabel ?? "Other" }
            .compactMap { parentLabel, children -> PlaybackFilterCategory? in
                guard let categoryID = children.first?.categoryID else { return nil }
                return PlaybackFilterCategory(
                    id: categoryID,
                    label: parentLabel,
                    icon: icons[parentLabel] ?? "shield",
                    groups: children.sorted { $0.label < $1.label }
                )
            }
            .sorted { $0.label < $1.label }
    }

    /// What the book screen reports next to "Filters".
    static func controlCount(_ events: [ScanEvent]) -> Int {
        available(events).reduce(0) { $0 + $1.controlCount }
    }
}
