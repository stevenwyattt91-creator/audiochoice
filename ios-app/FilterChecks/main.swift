import Foundation

// Exercises the per-book filter logic. Lives outside ios-app/AudioChoice so it does not
// join the app target.
//
// This code decides whether content a listener asked never to hear is removed, and every
// way it can go wrong is silent: a control that vanishes when switched off, a key that
// differs from the one Android saves, or a default that exposes everything.

var failures = 0
func check(_ description: String, _ condition: @autoclosure () -> Bool) {
    if condition() {
        print("  ok   \(description)")
    } else {
        print("  FAIL \(description)")
        failures += 1
    }
}

func categoryID(_ digit: Int) -> UUID {
    UUID(uuidString: "\(digit)0000000-0000-0000-0000-000000000001")!
}
func groupID(_ category: Int, _ group: Int) -> UUID {
    UUID(uuidString: "\(category)1000000-0000-0000-0000-" + String(format: "%012d", group))!
}

func event(
    category: Int,
    group: Int,
    start: Double = 10,
    end: Double = 12,
    stableKey: String? = nil,
    safeDescription: String? = "A described moment",
    aggregateKey: String? = nil,
    aggregateDisplay: String? = nil,
    id: UUID = UUID()
) -> ScanEvent {
    ScanEvent(
        id: id,
        startTime: start,
        endTime: end,
        categoryID: categoryID(category),
        groupID: groupID(category, group),
        eventID: UUID(),
        confidence: 0.9,
        stableKey: stableKey,
        safeDescription: safeDescription,
        aggregateKey: aggregateKey,
        aggregateDisplay: aggregateDisplay
    )
}

let strongProfanity = event(category: 2, group: 2, stableKey: "profanity-strong-1")
let gore = event(category: 3, group: 3, start: 40, end: 44, stableKey: "gore-1")
let mildViolence = event(category: 3, group: 1, start: 60, end: 62, stableKey: "mild-1")

print("Defaults")
// Everything the scan found is removed until the listener says otherwise. The opposite
// default would turn a book nobody has opened into unfiltered playback.
check("empty settings filter a detected event",
      BookFilterPredicate.shouldSkip(strongProfanity, settings: .everythingFiltered))
check("empty settings report no exceptions", !BookFilterSettings.everythingFiltered.hasExceptions)

print("Taxonomy coverage")
check("graphic violence is filterable", PlaybackFilterTaxonomy.isFilterable(gore))
check("torture is filterable", PlaybackFilterTaxonomy.isFilterable(event(category: 3, group: 4)))
check("violence involving children is filterable",
      PlaybackFilterTaxonomy.isFilterable(event(category: 3, group: 6)))
check("violence involving animals is filterable",
      PlaybackFilterTaxonomy.isFilterable(event(category: 3, group: 7)))
// Lower-severity violence has no switch anywhere, so removing it would take content away
// with nothing to turn it back on. This is the complaint about "the slightest things".
check("mild violence is not filterable", !PlaybackFilterTaxonomy.isFilterable(mildViolence))
check("mild violence is never skipped",
      !BookFilterPredicate.shouldSkip(mildViolence, settings: .everythingFiltered))
check("every filterable event has a control",
      PlaybackFilterTaxonomy.available([gore]).first?.groups.first?.controls.count == 1)

print("Switching a category off")
var hierarchy = PlaybackFilterTaxonomy.available([strongProfanity, gore])
var settings = BookFilterEditor.setCategory(
    categoryID(2).uuidString.lowercased(), enabled: false,
    in: .everythingFiltered, hierarchy: hierarchy
)
check("its events stop being filtered",
      !BookFilterPredicate.shouldSkip(strongProfanity, settings: settings))
check("another category keeps filtering", BookFilterPredicate.shouldSkip(gore, settings: settings))
check("the cascade reaches the group",
      settings.disabledGroupIDs.contains(groupID(2, 2).uuidString.lowercased()))
check("the cascade reaches the control", settings.disabledEventKeys.contains("profanity-strong-1"))

print("Switching a group back on inside a disabled category")
// The category entry would otherwise keep overriding the group, so the switch would
// appear to do nothing at all.
settings = BookFilterEditor.setGroup(
    groupID(2, 2).uuidString.lowercased(), enabled: true, in: settings, hierarchy: hierarchy
)
check("the group filters again", BookFilterPredicate.shouldSkip(strongProfanity, settings: settings))
check("the enclosing category is lifted",
      !settings.disabledCategoryIDs.contains(categoryID(2).uuidString.lowercased()))

print("Aggregate controls")
let repeated: [ScanEvent] = (0..<5).map { index in
    let start = 100.0 + Double(index) * 10.0
    return event(
        category: 2, group: 2, start: start, end: start + 1.0,
        aggregateKey: "word-damn", aggregateDisplay: "d**n"
    )
}
hierarchy = PlaybackFilterTaxonomy.available(repeated)
check("five occurrences collapse to one control", PlaybackFilterTaxonomy.controlCount(repeated) == 1)
check("the control reports its occurrences",
      hierarchy.first?.groups.first?.controls.first?.occurrences == 5)
check("the control is marked aggregate",
      hierarchy.first?.groups.first?.controls.first?.isAggregate == true)
var aggregateOff = BookFilterEditor.setControl(
    hierarchy[0].groups[0].controls[0], enabled: false,
    in: .everythingFiltered, hierarchy: hierarchy
)
check("switching it off stops every occurrence",
      repeated.allSatisfy { !BookFilterPredicate.shouldSkip($0, settings: aggregateOff) })

print("An aggregate spanning several groups")
// One word can be classified into more than one subgroup. It should appear once, while
// playback still removes every range.
let spanning = [
    event(category: 2, group: 2, start: 10, end: 11, aggregateKey: "word-x", aggregateDisplay: "x**"),
    event(category: 2, group: 3, start: 20, end: 21, aggregateKey: "word-x", aggregateDisplay: "x**")
]
check("it is listed once", PlaybackFilterTaxonomy.controlCount(spanning) == 1)
let spanningHierarchy = PlaybackFilterTaxonomy.available(spanning)
aggregateOff = BookFilterEditor.setControl(
    spanningHierarchy[0].groups[0].controls[0], enabled: false,
    in: .everythingFiltered, hierarchy: spanningHierarchy
)
check("switching it off covers both groups",
      spanning.allSatisfy { !BookFilterPredicate.shouldSkip($0, settings: aggregateOff) })

print("Disabled controls stay visible")
// If a switched-off control disappeared from the list there would be no way to switch it
// back on, which would make the choice permanent by accident.
let everythingOff = BookFilterEditor.setCategory(
    categoryID(2).uuidString.lowercased(), enabled: false,
    in: .everythingFiltered,
    hierarchy: PlaybackFilterTaxonomy.available([strongProfanity, gore])
)
let listed = PlaybackFilterTaxonomy.available([strongProfanity, gore])
check("the switched-off category is still listed",
      listed.contains { $0.id == categoryID(2).uuidString.lowercased() })
check("its control is still listed and is the one that was switched off",
      listed
        .first { $0.id == categoryID(2).uuidString.lowercased() }?
        .groups.first?.controls.contains { everythingOff.disabledEventKeys.contains($0.key) } == true)
check("that control does report itself as off",
      everythingOff.disabledEventKeys.contains("profanity-strong-1"))

print("Keys agree with the Android client")
// A GUID reaches the server lowercased; Swift's uuidString is uppercase. Using it
// unchanged would mean a control switched off on Android stayed on here.
let noStableKey = event(category: 2, group: 2, stableKey: nil)
check("the fallback key is lowercased",
      BookFilterSettings.eventKey(for: noStableKey) == noStableKey.id.uuidString.lowercased())
check("a stable key is used verbatim",
      BookFilterSettings.eventKey(for: strongProfanity) == "profanity-strong-1")
check("a blank aggregate key is not treated as an aggregate",
      BookFilterPredicate.shouldSkip(
        event(category: 2, group: 2, aggregateKey: ""),
        settings: BookFilterSettings(
            disabledCategoryIDs: [], disabledGroupIDs: [],
            disabledEventKeys: [], disabledAggregateKeys: [""]
        )
      ))

print("Individual controls")
let individualOff = BookFilterEditor.setControl(
    PlaybackFilterControl(
        key: "gore-1", label: "A described moment", occurrences: 1, startTime: 40, isAggregate: false
    ),
    enabled: false, in: .everythingFiltered,
    hierarchy: PlaybackFilterTaxonomy.available([gore])
)
check("one moment can be allowed through",
      !BookFilterPredicate.shouldSkip(gore, settings: individualOff))
check("its neighbours keep filtering",
      BookFilterPredicate.shouldSkip(strongProfanity, settings: individualOff))

print("Round trip through storage")
let bookID = UUID()
BookFilterSettingsStore.save(individualOff, bookID: bookID)
check("choices survive a reload", BookFilterSettingsStore.load(bookID) == individualOff)
check("an unknown book filters everything",
      BookFilterSettingsStore.load(UUID()) == .everythingFiltered)

print("")
if failures == 0 {
    print("All filter checks passed.")
} else {
    print("\(failures) filter check(s) failed.")
    exit(1)
}
