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

print("Scan results stored beside the library")
// Scans are the largest thing stored and used to sit inside the library blob, so every read
// of the library decoded every event of every book. They now live in their own files; what
// matters is that moving them cannot lose them.
func scan(_ eventCount: Int) -> ScanResult {
    ScanResult(
        events: (0..<eventCount).map { index in
            ScanEvent(
                id: UUID(), startTime: Double(index) * 10, endTime: Double(index) * 10 + 2,
                categoryID: UUID(uuidString: "20000000-0000-0000-0000-000000000001")!,
                groupID: UUID(uuidString: "21000000-0000-0000-0000-000000000002")!,
                eventID: UUID(), confidence: 0.9, stableKey: "key-\(index)",
                safeDescription: "Strong language", aggregateKey: nil, aggregateDisplay: nil
            )
        },
        scanDate: Date(),
        scannerVersion: "checks-1"
    )
}

var scanned = makeRecord(title: "Scanned Book")
AudiobookLibraryStore.upsert(scanned)
AudiobookLibraryStore.attach(result: scan(50), to: scanned.id)
check("a scan is readable straight after being attached",
      AudiobookLibraryStore.load().first { $0.id == scanned.id }?.scanResult?.events.count == 50)

// Proves it really came from the file rather than from the in-memory copy.
check("the scan is on disk by itself",
      ScanResultFileStore.load(scanned.id)?.events.count == 50)

// The point of the change: the blob must no longer carry the events.
let blob = UserDefaults.standard.data(forKey: "audiobookLibrary.v1") ?? Data()
let blobText = String(data: blob, encoding: .utf8) ?? ""
check("the library index does not contain scan events", !blobText.contains("Strong language"))
// Expressed against the scan's own size rather than a fixed number of bytes, so the check
// says what it means regardless of how many books happen to be stored.
let scanBytes = (try? Data(
    contentsOf: FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("ScanResults/\(scanned.id.uuidString).json")
))?.count ?? 0
check("the scan file holds the bulk of it", scanBytes > blob.count)

check("a scan survives an unrelated edit",
      AudiobookLibraryStore.rename("Renamed", for: scanned.id)?.scanResult?.events.count == 50)
check("marking finished does not drop the scan",
      AudiobookLibraryStore.setFinished(true, for: scanned.id)?.scanResult?.events.count == 50)

print("Records written before scans moved out")
// An older blob carries its scan inline. Replacing that with a file lookup that finds
// nothing would throw away the book's filter data, which is the one thing that must not
// happen here.
let legacyID = UUID()
let legacyBookID = UUID()
let inlineScan = scan(3)
let inlineEvents = try! JSONEncoder().encode(inlineScan)
let inlineText = String(data: inlineEvents, encoding: .utf8)!
let legacyBlob = """
[{"id":"\(legacyID.uuidString)","book":{"id":"\(legacyBookID.uuidString)","title":"Inline",\
"author":"A","progress":0,"timeRemaining":"","runtime":"1h","chapters":1,\
"edition":"Standard"},"fileSize":10,"importedAt":0,"scanResult":\(inlineText)}]
"""
UserDefaults.standard.set(Data(legacyBlob.utf8), forKey: "audiobookLibrary.v1")
AudiobookLibraryStore.invalidateCache()
check("an inline scan is still found", 
      AudiobookLibraryStore.load().first { $0.id == legacyID }?.scanResult?.events.count == 3)
check("it is migrated out to its own file",
      ScanResultFileStore.load(legacyID)?.events.count == 3)
AudiobookLibraryStore.invalidateCache()
check("and it survives the migration",
      AudiobookLibraryStore.load().first { $0.id == legacyID }?.scanResult?.events.count == 3)

print("Removing a book takes its scan with it")
let doomed = makeRecord(title: "Doomed")
AudiobookLibraryStore.upsert(doomed)
AudiobookLibraryStore.attach(result: scan(4), to: doomed.id)
check("its scan exists first", ScanResultFileStore.load(doomed.id) != nil)
AudiobookLibraryStore.remove(doomed)
check("the scan file is deleted too", ScanResultFileStore.load(doomed.id) == nil)
check("the book is gone", !AudiobookLibraryStore.load().contains { $0.id == doomed.id })

// Leaves nothing behind in Application Support.
for id in [scanned.id, legacyID] { ScanResultFileStore.remove(id) }

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
print("Time remaining at a chosen speed")
// The same expectations Android's ListeningTimeTest pins, so a change to one platform's
// arithmetic that is not made to the other is caught here rather than by a listener
// comparing two devices.
check("normal speed leaves the book's remaining length untouched",
      ListeningTime.remainingRealSeconds(remainingBookSeconds: 3600, rate: 1) == 3600)
check("an hour takes forty eight minutes at 1.25x",
      ListeningTime.remainingRealSeconds(remainingBookSeconds: 3600, rate: 1.25) == 2880)
check("an hour takes forty minutes at 1.5x",
      ListeningTime.remainingRealSeconds(remainingBookSeconds: 3600, rate: 1.5) == 2400)
check("an hour takes half an hour at 2x",
      ListeningTime.remainingRealSeconds(remainingBookSeconds: 3600, rate: 2) == 1800)
check("slowing a narrator down leaves longer to listen",
      ListeningTime.remainingRealSeconds(remainingBookSeconds: 3600, rate: 0.75) == 4800)
check("a finished book has nothing left at any speed",
      ListeningTime.remainingRealSeconds(remainingBookSeconds: 0, rate: 2) == 0)
check("a rate of zero reads as normal rather than dividing by it",
      ListeningTime.remainingRealSeconds(remainingBookSeconds: 3600, rate: 0) == 3600)
check("a duration that is not a number reports nothing left",
      ListeningTime.remainingRealSeconds(remainingBookSeconds: .nan, rate: 1) == 0)

print("")
if failures == 0 {
    print("All library checks passed.")
} else {
    print("\(failures) library check(s) failed.")
    exit(1)
}
