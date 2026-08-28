import Foundation

/// A listener's filter choices for one book.
///
/// Stored as what has been switched *off*, so an absent or empty record means every
/// detected event is still filtered. That direction is deliberate: the opposite
/// encoding would turn a failed load, or a book the listener has never opened, into
/// silently unfiltered playback.
///
/// The four levels are the ones the screen exposes: a whole category, one group
/// inside it, a single occurrence, or a repeated word collapsed into one control.
struct BookFilterSettings: Codable, Equatable {
    /// Category and group identifiers are held lowercased, because the server is not
    /// consistent about GUID casing and a case difference would silently fail to match.
    var disabledCategoryIDs: Set<String> = []
    var disabledGroupIDs: Set<String> = []
    /// Event and aggregate keys are held as the scanner emits them. Normalising these
    /// would change which previously saved choices still match.
    var disabledEventKeys: Set<String> = []
    var disabledAggregateKeys: Set<String> = []

    static let everythingFiltered = BookFilterSettings()

    var hasExceptions: Bool {
        !disabledCategoryIDs.isEmpty || !disabledGroupIDs.isEmpty
            || !disabledEventKeys.isEmpty || !disabledAggregateKeys.isEmpty
    }

    /// The key that identifies a single, non-aggregated event.
    ///
    /// Lowercased when falling back to the identifier because that is how the server
    /// writes a GUID in JSON, and therefore what the Android client saves. Swift's
    /// `UUID.uuidString` is uppercase, so using it unchanged would mean a control
    /// switched off on one platform stayed on on the other.
    static func eventKey(for event: ScanEvent) -> String {
        if let stableKey = event.stableKey, !stableKey.isEmpty { return stableKey }
        return event.id.uuidString.lowercased()
    }
}

/// The single definition of "is this filter still switched on for this book".
///
/// Both playback and the reader's masking pass go through here. Writing the condition
/// out twice is how audio and text end up disagreeing about what is filtered, which
/// would show a listener the words they asked not to hear.
enum BookFilterPredicate {
    /// Whether playback removes this event, given the listener's choices for this book.
    ///
    /// Two independent conditions. The taxonomy decides whether the event is something
    /// the app filters at all, and the settings decide whether the listener has since
    /// switched it off.
    static func shouldSkip(_ event: ScanEvent, settings: BookFilterSettings) -> Bool {
        guard PlaybackFilterTaxonomy.isFilterable(event) else { return false }
        return isEnabled(event, settings: settings)
    }

    static func isEnabled(_ event: ScanEvent, settings: BookFilterSettings) -> Bool {
        if settings.disabledCategoryIDs.contains(event.categoryID.uuidString.lowercased()) {
            return false
        }
        if settings.disabledGroupIDs.contains(event.groupID.uuidString.lowercased()) {
            return false
        }
        if settings.disabledEventKeys.contains(BookFilterSettings.eventKey(for: event)) {
            return false
        }
        // An empty aggregate key means "not an aggregate". Treating it as one would let
        // a blank entry match, and the taxonomy already counts blank-key events as
        // individual controls.
        if let aggregateKey = event.aggregateKey, !aggregateKey.isEmpty,
           settings.disabledAggregateKeys.contains(aggregateKey) {
            return false
        }
        return true
    }
}

/// Applies a switch at one level of the hierarchy, cascading to everything beneath it.
///
/// Turning a category off has to reach its groups and their controls, otherwise the
/// parent switch would read as off while the children still showed on.
enum BookFilterEditor {
    static func setCategory(
        _ categoryID: String,
        enabled: Bool,
        in settings: BookFilterSettings,
        hierarchy: [PlaybackFilterCategory]
    ) -> BookFilterSettings {
        var updated = settings
        let category = categoryID.lowercased()
        apply(enabled, category, to: &updated.disabledCategoryIDs)

        guard let parent = hierarchy.first(where: { $0.id == category }) else { return updated }
        for group in parent.groups {
            apply(enabled, group.id, to: &updated.disabledGroupIDs)
            for control in group.controls { apply(enabled, control, to: &updated) }
        }
        return updated
    }

    static func setGroup(
        _ groupID: String,
        enabled: Bool,
        in settings: BookFilterSettings,
        hierarchy: [PlaybackFilterCategory]
    ) -> BookFilterSettings {
        var updated = settings
        let group = groupID.lowercased()
        apply(enabled, group, to: &updated.disabledGroupIDs)

        // Re-enabling a group inside a switched-off category has to lift the category
        // too, or the category entry would keep overriding it and the switch would
        // appear to do nothing.
        if enabled,
           let owner = hierarchy.first(where: { $0.groups.contains(where: { $0.id == group }) }) {
            updated.disabledCategoryIDs.remove(owner.id)
        }

        guard let child = hierarchy.flatMap(\.groups).first(where: { $0.id == group }) else {
            return updated
        }
        for control in child.controls { apply(enabled, control, to: &updated) }
        return updated
    }

    static func setControl(
        _ control: PlaybackFilterControl,
        enabled: Bool,
        in settings: BookFilterSettings,
        hierarchy: [PlaybackFilterCategory]
    ) -> BookFilterSettings {
        var updated = settings
        apply(enabled, control, to: &updated)

        guard enabled else { return updated }
        // Same reasoning as setGroup: an enclosing group or category that is still
        // switched off would keep this control suppressed whatever its own switch says.
        for category in hierarchy {
            for group in category.groups where group.controls.contains(control) {
                updated.disabledGroupIDs.remove(group.id)
                updated.disabledCategoryIDs.remove(category.id)
            }
        }
        return updated
    }

    private static func apply(_ enabled: Bool, _ value: String, to values: inout Set<String>) {
        if enabled { values.remove(value) } else { values.insert(value) }
    }

    private static func apply(
        _ enabled: Bool,
        _ control: PlaybackFilterControl,
        to settings: inout BookFilterSettings
    ) {
        if control.isAggregate {
            apply(enabled, control.key, to: &settings.disabledAggregateKeys)
        } else {
            apply(enabled, control.key, to: &settings.disabledEventKeys)
        }
    }
}

/// Where a book's filter choices live on this device.
///
/// Local storage is the source of truth for playback so that filtering keeps working
/// offline. The server copy exists to follow the listener to another device.
enum BookFilterSettingsStore {
    private static func key(_ bookID: UUID) -> String {
        "bookFilters.v1.\(bookID.uuidString)"
    }

    static func load(_ bookID: UUID) -> BookFilterSettings {
        guard let data = UserDefaults.standard.data(forKey: key(bookID)),
              let value = try? JSONDecoder().decode(BookFilterSettings.self, from: data)
        else { return .everythingFiltered }
        return value
    }

    static func save(_ settings: BookFilterSettings, bookID: UUID) {
        guard let data = try? JSONEncoder().encode(settings) else { return }
        UserDefaults.standard.set(data, forKey: key(bookID))
    }
}
