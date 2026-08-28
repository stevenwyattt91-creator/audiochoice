import Foundation

/// Turns a saved profile into the starting choices for a book, and back again.
///
/// The point of a profile is that "what I never want to hear" is said once rather than per
/// book. What can travel between books, though, is narrower than what a book's own settings
/// hold: categories and groups are taxonomy identifiers and mean the same thing everywhere,
/// while an event key names one moment in one recording and an aggregate key names one word
/// as it occurs in that recording. Carrying those into a profile would either do nothing or,
/// worse, collide with an unrelated key in another book, so they are left out.
enum FilterProfileMapping {

    /// The choices a profile hands a book it has never seen.
    static func settings(
        from profile: FilterProfile,
        taxonomy: (category: Set<String>, group: Set<String>)
    ) -> BookFilterSettings {
        var settings = BookFilterSettings.everythingFiltered
        for rule in profile.rules where !rule.enabled {
            let key = rule.key.lowercased()
            // Resolved against the taxonomy rather than trusting a stored level, so a key
            // that no longer exists is ignored instead of disabling something unintended.
            if taxonomy.category.contains(key) {
                settings.disabledCategoryIDs.insert(key)
            } else if taxonomy.group.contains(key) {
                settings.disabledGroupIDs.insert(key)
            }
        }
        return settings
    }

    /// What to save when a listener turns this book's choices into a profile.
    ///
    /// Only the two portable levels are kept. A listener who allowed one specific line
    /// through in one book is not saying they want that everywhere.
    static func rules(from settings: BookFilterSettings) -> [FilterRule] {
        (settings.disabledCategoryIDs.sorted() + settings.disabledGroupIDs.sorted())
            .map(FilterRule.disabled)
    }

    /// Every category and group identifier the app understands.
    ///
    /// Built from the same table that draws the filter screen, so a profile can never
    /// disable something the listener has no switch for.
    static func knownTaxonomy() -> (category: Set<String>, group: Set<String>) {
        (PlaybackFilterTaxonomy.knownCategoryIDs, PlaybackFilterTaxonomy.knownGroupIDs)
    }
}
