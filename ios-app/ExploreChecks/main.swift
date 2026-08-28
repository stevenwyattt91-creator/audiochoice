import Foundation

// Exercises the explore catalogue cleanup. Lives outside ios-app/AudioChoice so it does not
// join the app target.
//
// The risk runs both ways. Merging too little leaves the duplicates that prompted this;
// merging too much hides a genuinely different recording behind another one's entry, and
// its filter scan does not apply to the audio someone then plays.

var failures = 0
func check(_ description: String, _ condition: @autoclosure () -> Bool) {
    if condition() {
        print("  ok   \(description)")
    } else {
        print("  FAIL \(description)")
        failures += 1
    }
}

func book(
    _ catalogID: String,
    _ title: String,
    author: String? = nil,
    editionType: String? = nil,
    duration: Double? = 3600,
    eventCount: Int = 10,
    cover: String? = nil,
    description: String? = nil
) -> ExploreCatalogBook {
    ExploreCatalogBook(
        catalogID: catalogID,
        title: title,
        author: author,
        editionType: editionType,
        duration: duration,
        fileType: "m4b",
        eventCount: eventCount,
        coverImageURL: cover,
        description: description,
        purchaseURL: URL(string: "https://example.com")!,
        purchaseProvider: "example"
    )
}

print("Title normalisation")
check("an unabridged suffix is ignored",
      ExploreCatalogCleanup.normalizedTitle("Fourth Wing (Unabridged)")
        == ExploreCatalogCleanup.normalizedTitle("Fourth Wing"))
check("a bracketed note is ignored",
      ExploreCatalogCleanup.normalizedTitle("Fourth Wing [Dramatized Adaptation]")
        == ExploreCatalogCleanup.normalizedTitle("Fourth Wing"))
check("punctuation differences are ignored",
      ExploreCatalogCleanup.normalizedTitle("King Sorrow: A Novel")
        == ExploreCatalogCleanup.normalizedTitle("King Sorrow - A Novel"))
check("a trailing audiobook suffix is ignored",
      ExploreCatalogCleanup.normalizedTitle("Mistborn Audiobook")
        == ExploreCatalogCleanup.normalizedTitle("Mistborn"))
// A word that happens to appear mid-title is not a suffix.
check("a genuine word inside a title survives",
      ExploreCatalogCleanup.normalizedTitle("The Abridged History of Everything")
        != ExploreCatalogCleanup.normalizedTitle("The History of Everything"))
check("different books stay different",
      ExploreCatalogCleanup.normalizedTitle("Fourth Wing")
        != ExploreCatalogCleanup.normalizedTitle("Iron Flame"))

print("Part markers")
check("a part is detected", ExploreCatalogCleanup.partMarker("Fourth Wing 1 of 2") == "1 of 2")
check("a written-out part is detected",
      ExploreCatalogCleanup.partMarker("Fourth Wing Part 2 of 2") == "2 of 2")
check("a book with no part reports none",
      ExploreCatalogCleanup.partMarker("Fourth Wing") == nil)

print("Merging the same recording")
var merged = ExploreCatalogCleanup.deduplicated([
    book("a", "Fourth Wing", author: "Rebecca Yarros", cover: "/v1/explore/a/cover"),
    book("b", "Fourth Wing (Unabridged)", author: "Rebecca Yarros"),
    book("c", "Fourth Wing", author: nil)
])
check("three spellings become one entry", merged.count == 1)
// An untagged author is a missing tag, not a different book.
check("the entry with the cover is kept", merged.first?.catalogID == "a")

print("Keeping genuinely different recordings apart")
// Different audio, different runtime, different scan. Collapsing these would show one
// edition's filter data against the other's audio.
var separate = ExploreCatalogCleanup.deduplicated([
    book("a", "Fourth Wing", author: "Rebecca Yarros", editionType: "GraphicAudio"),
    book("b", "Fourth Wing", author: "Rebecca Yarros")
])
check("different edition types stay separate", separate.count == 2)

separate = ExploreCatalogCleanup.deduplicated([
    book("a", "Fourth Wing 1 of 2", author: "Rebecca Yarros"),
    book("b", "Fourth Wing 2 of 2", author: "Rebecca Yarros")
])
check("separate parts stay separate", separate.count == 2)

separate = ExploreCatalogCleanup.deduplicated([
    book("a", "Fourth Wing", author: "Rebecca Yarros"),
    book("b", "Fourth Wing", author: "Someone Else")
])
check("two different authors stay separate", separate.count == 2)

print("Choosing which entry to keep")
merged = ExploreCatalogCleanup.deduplicated([
    book("a", "King Sorrow", author: "Joe Hill", eventCount: 3),
    book("b", "King Sorrow", author: "Joe Hill", eventCount: 41)
])
check("the richer scan wins when neither has a cover", merged.first?.catalogID == "b")

merged = ExploreCatalogCleanup.deduplicated([
    book("a", "King Sorrow", author: "Joe Hill", eventCount: 900),
    book("b", "King Sorrow", author: "Joe Hill", eventCount: 3, cover: "/cover")
])
check("a cover outranks a richer scan", merged.first?.catalogID == "b")

merged = ExploreCatalogCleanup.deduplicated([
    book("a", "King Sorrow", author: nil, duration: nil),
    book("b", "King Sorrow", author: "Joe Hill", description: "A novel")
])
check("the entry that can describe itself wins", merged.first?.catalogID == "b")

print("Stability")
let unordered = [
    book("z", "King Sorrow", author: "Joe Hill"),
    book("a", "King Sorrow", author: "Joe Hill")
]
check("equal entries resolve the same way regardless of order",
      ExploreCatalogCleanup.deduplicated(unordered).first?.catalogID
        == ExploreCatalogCleanup.deduplicated(unordered.reversed()).first?.catalogID)

let catalogue = [
    book("1", "Alpha", author: "A"),
    book("2", "Beta", author: "B"),
    book("3", "Gamma", author: "C")
]
check("a catalogue with no duplicates is untouched",
      ExploreCatalogCleanup.deduplicated(catalogue).map(\.catalogID) == ["1", "2", "3"])
check("the server's title order is preserved",
      ExploreCatalogCleanup.deduplicated([
        book("1", "Alpha", author: "A"),
        book("2", "Alpha (Unabridged)", author: "A"),
        book("3", "Beta", author: "B")
      ]).map(\.catalogID) == ["1", "3"])
check("an empty catalogue is handled", ExploreCatalogCleanup.deduplicated([]).isEmpty)

print("")
if failures == 0 {
    print("All explore checks passed.")
} else {
    print("\(failures) explore check(s) failed.")
    exit(1)
}
