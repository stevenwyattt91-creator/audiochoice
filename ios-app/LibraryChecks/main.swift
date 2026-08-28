import Foundation

// Exercises the completion rule and the rename-versus-identity split. Lives outside
// ios-app/AudioChoice so it does not join the app target.
//
// Renaming is the risk worth covering: the displayed title is editable, but the title sent
// as identity evidence must keep being the file's own, or a listener correcting a label
// could quietly change which recording the book is matched to, and with it which filters
// and transcript apply.

/// Stands in for the import service, which MobileModels calls only to delete files on
/// removal. Compiling the real one would pull in AVFoundation and the whole import stack.
enum AudiobookImportService {
    static func audioURL(fileName: String) -> URL { URL(fileURLWithPath: "/tmp/\(fileName)") }
    static func artworkURL(fileName: String) -> URL { URL(fileURLWithPath: "/tmp/\(fileName)") }
}

var failures = 0
func check(_ description: String, _ condition: @autoclosure () -> Bool) {
    if condition() {
        print("  ok   \(description)")
    } else {
        print("  FAIL \(description)")
        failures += 1
    }
}

func makeBook(title: String) -> MobileBook {
    MobileBook(
        title: title, author: "Joe Hill", progress: 0,
        timeRemaining: "", runtime: "20h", chapters: 40, edition: "Standard"
    )
}

func makeRecord(title: String) -> LibraryBookRecord {
    LibraryBookRecord(
        id: UUID(), book: makeBook(title: title),
        localFileName: "audio.m4b", artworkFileName: nil,
        fileSize: 1024, importedAt: Date()
    )
}

print("Completion threshold")
// A book that has only just opened reports position 0 against duration 0. Treating that
// as finished would mark everything complete on sight.
check("an unloaded player is not finished", !BookCompletion.isComplete(position: 0, duration: 0))
check("position without duration is not finished",
      !BookCompletion.isComplete(position: 500, duration: 0))
check("the start of a book is not finished",
      !BookCompletion.isComplete(position: 0, duration: 36000))
check("the middle of a book is not finished",
      !BookCompletion.isComplete(position: 18000, duration: 36000))
check("the last moment is finished",
      BookCompletion.isComplete(position: 36000, duration: 36000))
// Books end with credits and an outro that listeners stop before.
check("within 30 seconds of the end is finished",
      BookCompletion.isComplete(position: 35975, duration: 36000))
check("a minute from the end is not finished",
      !BookCompletion.isComplete(position: 35940, duration: 36000))

print("Short files")
// A 20 second file would give a negative threshold on the 30 second rule, which would have
// marked it finished the instant it opened.
check("a short file is not finished at the start",
      !BookCompletion.isComplete(position: 0.5, duration: 20))
check("a short file is not finished mid-way",
      !BookCompletion.isComplete(position: 10, duration: 20))
check("a short file is finished at its end",
      BookCompletion.isComplete(position: 20, duration: 20))
check("the threshold is never negative", BookCompletion.threshold(duration: 20) > 0)

print("Renaming keeps identity")
var record = makeRecord(title: "King Sorrow - Joe Hill")
AudiobookLibraryStore.upsert(record)
check("evidence is the file's title before any rename",
      record.evidenceTitle == "King Sorrow - Joe Hill")

guard let renamed = AudiobookLibraryStore.rename("King Sorrow", for: record.id) else {
    print("  FAIL rename returned nothing")
    exit(1)
}
check("the displayed title changes", renamed.book.title == "King Sorrow")
// The whole point: identification keeps using what the file said.
check("evidence still reports the file's title",
      renamed.evidenceTitle == "King Sorrow - Joe Hill")
check("the original was captured once", renamed.identityTitle == "King Sorrow - Joe Hill")

// A second rename must not overwrite the captured original with the first correction.
guard let twice = AudiobookLibraryStore.rename("King Sorrow (Unabridged)", for: record.id) else {
    print("  FAIL second rename returned nothing")
    exit(1)
}
check("a second rename changes the display", twice.book.title == "King Sorrow (Unabridged)")
check("a second rename keeps the original evidence",
      twice.evidenceTitle == "King Sorrow - Joe Hill")

print("Rename input handling")
check("whitespace is trimmed",
      AudiobookLibraryStore.rename("  Trimmed  ", for: record.id)?.book.title == "Trimmed")
check("an empty title is refused", AudiobookLibraryStore.rename("   ", for: record.id) == nil)
check("refusing leaves the previous title",
      AudiobookLibraryStore.load().first { $0.id == record.id }?.book.title == "Trimmed")
check("an unknown book cannot be renamed",
      AudiobookLibraryStore.rename("Nothing", for: UUID()) == nil)

print("Marking finished")
record = makeRecord(title: "Another Book")
AudiobookLibraryStore.upsert(record)
check("a new book is not finished", record.isFinished == false)
check("marking finished is stored",
      AudiobookLibraryStore.setFinished(true, for: record.id)?.isFinished == true)
check("it survives a reload",
      AudiobookLibraryStore.load().first { $0.id == record.id }?.isFinished == true)
check("unmarking is stored",
      AudiobookLibraryStore.setFinished(false, for: record.id)?.isFinished == false)
check("an unknown book cannot be marked",
      AudiobookLibraryStore.setFinished(true, for: UUID()) == nil)

print("Older records still decode")
// Records written before these fields existed must load rather than being dropped, which
// would empty someone's library on update.
let legacy = """
[{"id":"\(UUID().uuidString)","book":{"id":"\(UUID().uuidString)","title":"Legacy",\
"author":"A","progress":0,"timeRemaining":"","runtime":"1h","chapters":1,\
"edition":"Standard"},"fileSize":10,"importedAt":0}]
"""
var decoded: [LibraryBookRecord]?
do {
    decoded = try JSONDecoder().decode([LibraryBookRecord].self, from: Data(legacy.utf8))
} catch {
    print("  (decode error: \(error))")
    decoded = nil
}
check("a record without the new fields decodes", decoded?.count == 1)
check("it defaults to not finished", decoded?.first?.isFinished == false)
check("its evidence falls back to the displayed title",
      decoded?.first?.evidenceTitle == "Legacy")

print("")
if failures == 0 {
    print("All library checks passed.")
} else {
    print("\(failures) library check(s) failed.")
    exit(1)
}
