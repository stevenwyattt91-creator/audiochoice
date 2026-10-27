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

    // Sexual content only, permanently -- see the backend's ContentTaxonomy.EnforcedLabels
    // remarks. Profanity, Violence, Drugs & Alcohol, Blasphemy, and Self-Harm & Suicide are
    // removed here too, not just left to stop appearing on their own: a book scanned before
    // this change may still carry old events in those groups, and this makes sure none of
    // them render a switch even for those, matching the backend's own permanent removal and
    // the Android client's identical change to its own copy of this table.
    private static let definitions: [String: Definition] = {
        var values: [String: Definition] = [:]

        func add(_ category: Int, _ parentLabel: String, _ childLabels: [String]) {
            for (index, childLabel) in childLabels.enumerated() {
                values[groupID(category: category, group: index + 1)] =
                    Definition(parentLabel: parentLabel, childLabel: childLabel)
            }
        }

        // Narrowed a second time, matching the backend's own second narrowing of
        // EnforcedLabels and the Android client's identical change: exactly three real
        // switches now (Nudity, group 3; Explicit/vulgar scenes, the newly-added group 9;
        // Sexual violence, group 7). Suggestive dialogue, Sexual references, Implied sexual
        // activity, Explicit sexual activity (the old, broader version), Complete sex
        // scenes, and Kissing are removed from here the same way the five non-sexual
        // categories were removed above -- a book scanned before this change may still
        // carry old events under those groups, and this makes sure none of them render a
        // switch any more either. Explicit group numbers, not the add() helper's sequential
        // indexing, since the real numbers (3, 7, 9) are not consecutive starting at 1.
        values[groupID(category: 1, group: 3)] = Definition(parentLabel: "Sexual Content", childLabel: "Nudity")
        values[groupID(category: 1, group: 9)] = Definition(parentLabel: "Sexual Content", childLabel: "Explicit / vulgar sex scenes")
        values[groupID(category: 1, group: 7)] = Definition(parentLabel: "Sexual Content", childLabel: "Sexual violence (rape / sexual assault)")
        return values
    }()

    private static let icons: [String: String] = [
        "Sexual Content": "heart.slash"
    ]

    /// Explicit/vulgar sex scenes and sexual violence are never allowed to lose the
    /// aggregate tie-break to a lower-rung label sharing the same aggregateKey. Ported from
    /// the same fix on the Android client, which found this as a real production bug: a
    /// 124-second, 0.95-confidence scene event shared an aggregateKey with four much
    /// shorter Nudity events from the same passage, and "first chronologically wins" picked
    /// the minor label, hiding the scene control entirely for that passage. These two
    /// groupIDs are the only ones a listener must always be able to find and turn off for a
    /// given passage, however many lesser labels happen to overlap it.
    private static let neverSuppressedGroupIDs: Set<String> = [
        groupID(category: 1, group: 9), // sexual_explicit_vulgar_scene
        groupID(category: 1, group: 7), // sexual_violence
    ]

    /// Whether playback removes this event at all, before the listener's choices apply.
    ///
    /// An event outside the definitions above has no switch anywhere in the app, so
    /// removing it would take content away with nothing to turn it back on.
    static func isFilterable(_ event: ScanEvent) -> Bool {
        definitions[event.groupID.uuidString.lowercased()] != nil
    }

    /// Every group the app has a switch for.
    ///
    /// Used to check a saved profile's keys, so a profile can never switch off something the
    /// listener has no control for.
    static var knownGroupIDs: Set<String> { Set(definitions.keys) }

    /// Every category the app has a switch for.
    ///
    /// Derived from the group identifiers rather than listed again. The scanner builds both
    /// from the same category digit, and a second hand-written list is how the two would come
    /// to disagree.
    static var knownCategoryIDs: Set<String> {
        Set(definitions.keys.compactMap { groupID in
            guard let digit = groupID.first else { return nil }
            return "\(digit)0000000-0000-0000-0000-000000000001"
        })
    }

    /// The categories, groups and controls present in this book, whether or not the
    /// listener has since switched any of them off.
    ///
    /// Disabled controls stay in the list on purpose: dropping them would make a
    /// switch vanish the moment it was turned off, leaving no way to turn it back on.
    static func available(_ events: [ScanEvent]) -> [PlaybackFilterCategory] {
        // One aggregate can span several taxonomy subgroups. Show it once, preferring a
        // never-suppressed subgroup (see neverSuppressedGroupIDs) over any other sharing the
        // same aggregateKey, then falling back to the first chronological subgroup, while
        // playback keeps every event's own range regardless.
        var preferredGroupByKey: [String: String] = [:]
        for event in events.sorted(by: { $0.startTime < $1.startTime }) {
            guard let key = event.aggregateKey, !key.isEmpty else { continue }
            let group = event.groupID.uuidString.lowercased()
            let current = preferredGroupByKey[key]
            let eventIsNeverSuppressed = neverSuppressedGroupIDs.contains(group)
            let currentIsNeverSuppressed = current.map { neverSuppressedGroupIDs.contains($0) } ?? false
            if current == nil || (eventIsNeverSuppressed && !currentIsNeverSuppressed) {
                preferredGroupByKey[key] = group
            }
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
