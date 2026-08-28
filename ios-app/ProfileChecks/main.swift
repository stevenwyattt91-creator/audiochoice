import Foundation

// Exercises how a saved profile becomes a new book's starting filters. Lives outside
// ios-app/AudioChoice so it does not join the app target.
//
// A profile decides what gets removed from books the listener has not looked at yet, so the
// risk is a key meaning something different in another book, or a profile quietly switching
// off protection nobody asked it to.

var failures = 0
func check(_ description: String, _ condition: @autoclosure () -> Bool) {
    if condition() {
        print("  ok   \(description)")
    } else {
        print("  FAIL \(description)")
        failures += 1
    }
}

let profanityCategory = "20000000-0000-0000-0000-000000000001"
let strongProfanityGroup = "21000000-0000-0000-0000-000000000002"
let violenceCategory = "30000000-0000-0000-0000-000000000001"
let taxonomy = FilterProfileMapping.knownTaxonomy()

func profile(_ rules: [FilterRule], name: String = "Mine", active: Bool = true) -> FilterProfile {
    FilterProfile(
        id: UUID(), name: name, isActive: active, rules: rules, customWords: [],
        createdAt: Date(), updatedAt: Date()
    )
}

print("The taxonomy a profile is checked against")
check("categories are known", taxonomy.category.contains(profanityCategory))
check("groups are known", taxonomy.group.contains(strongProfanityGroup))
check("violence is a known category", taxonomy.category.contains(violenceCategory))
// Derived from the group identifiers, so the two lists cannot drift apart.
check("there is a category for every category with groups", taxonomy.category.count == 6)

print("Turning a profile into a book's starting choices")
var settings = FilterProfileMapping.settings(
    from: profile([.disabled(profanityCategory), .disabled(strongProfanityGroup)]),
    taxonomy: taxonomy
)
check("a category key lands in the categories",
      settings.disabledCategoryIDs.contains(profanityCategory))
check("a group key lands in the groups",
      settings.disabledGroupIDs.contains(strongProfanityGroup))
// Nothing else may be switched off by implication.
check("nothing else is switched off",
      settings.disabledCategoryIDs.count == 1 && settings.disabledGroupIDs.count == 1)
check("no event keys are invented", settings.disabledEventKeys.isEmpty)
check("no aggregate keys are invented", settings.disabledAggregateKeys.isEmpty)

print("Rules that are still on")
settings = FilterProfileMapping.settings(
    from: profile([
        FilterRule(key: profanityCategory, enabled: true, action: "skip", severity: "all"),
        .disabled(violenceCategory)
    ]),
    taxonomy: taxonomy
)
check("an enabled rule does not switch anything off",
      !settings.disabledCategoryIDs.contains(profanityCategory))
check("a disabled rule alongside it still applies",
      settings.disabledCategoryIDs.contains(violenceCategory))

print("Keys the app does not recognise")
// A profile from a newer build, or a taxonomy that has moved on. Ignoring the key is the only
// safe reading: acting on it could remove content with no switch to bring it back.
settings = FilterProfileMapping.settings(
    from: profile([.disabled("99999999-9999-9999-9999-999999999999")]),
    taxonomy: taxonomy
)
check("an unknown key is ignored", !settings.hasExceptions)

// An event key belongs to one moment in one recording, so it must never travel.
settings = FilterProfileMapping.settings(
    from: profile([.disabled("profanity-strong-1"), .disabled("word-damn")]),
    taxonomy: taxonomy
)
check("a book-specific key is ignored", !settings.hasExceptions)

print("Casing")
settings = FilterProfileMapping.settings(
    from: profile([.disabled(profanityCategory.uppercased())]),
    taxonomy: taxonomy
)
check("an uppercased key still matches",
      settings.disabledCategoryIDs.contains(profanityCategory))

print("Saving a book's choices as a profile")
var book = BookFilterSettings.everythingFiltered
book.disabledCategoryIDs = [profanityCategory]
book.disabledGroupIDs = [strongProfanityGroup]
book.disabledEventKeys = ["one-line-in-this-book"]
book.disabledAggregateKeys = ["one-word-in-this-book"]
let rules = FilterProfileMapping.rules(from: book)
check("the portable levels are saved", rules.count == 2)
check("every saved rule is a switched-off one", rules.allSatisfy { !$0.enabled })
// Allowing one line through in one book is not a statement about every book.
check("the book's own event key is not saved",
      !rules.contains { $0.key == "one-line-in-this-book" })
check("the book's own word key is not saved",
      !rules.contains { $0.key == "one-word-in-this-book" })

print("A round trip")
let restored = FilterProfileMapping.settings(from: profile(rules), taxonomy: taxonomy)
check("the categories come back", restored.disabledCategoryIDs == book.disabledCategoryIDs)
check("the groups come back", restored.disabledGroupIDs == book.disabledGroupIDs)
check("and the book-specific choices did not", 
      restored.disabledEventKeys.isEmpty && restored.disabledAggregateKeys.isEmpty)

print("An empty profile")
check("filters everything, like an untouched book",
      !FilterProfileMapping.settings(from: profile([]), taxonomy: taxonomy).hasExceptions)
check("a profile of nothing switched off saves no rules",
      FilterProfileMapping.rules(from: .everythingFiltered).isEmpty)

print("")
if failures == 0 {
    print("All profile checks passed.")
} else {
    print("\(failures) profile check(s) failed.")
    exit(1)
}
