import Foundation

// Exercises what a filter report says. Lives outside ios-app/AudioChoice so it does not join
// the app target.
//
// Two properties matter. A report has to describe the passage the listener actually heard,
// which is behind them by the time they tap; and it must never carry the content itself,
// because a listener's audio staying on their device is the promise the whole app rests on.

var failures = 0
func check(_ description: String, _ condition: @autoclosure () -> Bool) {
    if condition() {
        print("  ok   \(description)")
    } else {
        print("  FAIL \(description)")
        failures += 1
    }
}

let fingerprint = BookFingerprint(
    version: 3, sha256: String(repeating: "a", count: 64), fileSize: 12_345,
    duration: 36_000, fileType: "m4b", workTitle: "King Sorrow", author: "Joe Hill",
    seriesTitle: nil, seriesNumber: nil, editionType: nil, partNumber: nil, totalParts: nil
)

func event(start: Double, end: Double) -> ScanEvent {
    ScanEvent(
        id: UUID(), startTime: start, endTime: end,
        categoryID: UUID(uuidString: "20000000-0000-0000-0000-000000000001")!,
        groupID: UUID(uuidString: "21000000-0000-0000-0000-000000000002")!,
        eventID: UUID(), confidence: 0.9, stableKey: "key-1",
        safeDescription: "Strong language", aggregateKey: nil, aggregateDisplay: nil
    )
}

print("Reporting something that was missed")
var report = FilterReportComposer.missedContent(
    fingerprint: fingerprint, position: 1234.5, scannerVersion: "scanner-7"
)
check("the kind is a missed passage", report.kind == .missedContent)
check("the tap position is kept", report.positionSeconds == 1234.5)
// The listener taps after the fact, so the report has to reach backwards.
check("it looks back rather than describing an instant",
      report.windowSeconds == FilterReportComposer.lookBackSeconds)
check("the look-back is long enough to contain a reaction", (report.windowSeconds ?? 0) >= 10)
check("the scan that produced the result is named", report.scannerVersion == "scanner-7")
check("no event is claimed, because none fired", report.scanEventID == nil)
check("no category is invented", report.categoryID == nil)

print("Refining a report afterwards")
let categoryID = UUID(uuidString: "20000000-0000-0000-0000-000000000001")!
report = FilterReportComposer.missedContent(
    fingerprint: fingerprint, position: 10, scannerVersion: nil, categoryID: categoryID
)
check("a chosen category is carried", report.categoryID == categoryID)
check("a missing scanner version is allowed", report.scannerVersion == nil)

print("Guarding against nonsense positions")
check("a negative position is clamped",
      FilterReportComposer.missedContent(
        fingerprint: fingerprint, position: -20, scannerVersion: nil
      ).positionSeconds == 0)

print("Reporting something wrongly removed")
let skipped = event(start: 900, end: 906)
report = FilterReportComposer.wronglyFiltered(
    fingerprint: fingerprint, event: skipped, scannerVersion: "scanner-7"
)
check("the kind is a wrong removal", report.kind == .wronglyFiltered)
// Naming the event is what makes over-filtering fixable: a timestamp alone leaves the
// control that fired to be guessed at.
check("the event that fired is named", report.scanEventID == skipped.id)
check("it starts at the flagged range, not the listener's position",
      report.positionSeconds == 900)
check("the window covers the flagged range", report.windowSeconds == 6)
check("the category comes from the event", report.categoryID == skipped.categoryID)

print("Window bounds")
check("a very long flagged range is clamped",
      (FilterReportComposer.wronglyFiltered(
        fingerprint: fingerprint, event: event(start: 0, end: 100_000), scannerVersion: nil
      ).windowSeconds ?? 0) <= 120)
check("a zero-length range still gets a usable window",
      (FilterReportComposer.wronglyFiltered(
        fingerprint: fingerprint, event: event(start: 50, end: 50), scannerVersion: nil
      ).windowSeconds ?? 0) >= 1)

print("What a report is allowed to contain")
// The report is encoded and inspected as JSON, because that is what actually leaves the
// device. A field added later that carried text would show up here.
let encoded = try! JSONEncoder().encode(
    FilterReportComposer.wronglyFiltered(
        fingerprint: fingerprint, event: skipped, scannerVersion: "scanner-7"
    )
)
let json = try! JSONSerialization.jsonObject(with: encoded) as! [String: Any]
let allowed: Set<String> = [
    "fingerprint", "kind", "positionSeconds", "windowSeconds",
    "scannerVersion", "scanEventID", "categoryID"
]
check("it carries only the agreed fields", Set(json.keys).isSubset(of: allowed))
let text = String(data: encoded, encoding: .utf8) ?? ""
check("the safe description is not sent", !text.contains("Strong language"))
check("no transcript or word field appears",
      !text.lowercased().contains("transcript") && !text.lowercased().contains("\"words\""))

print("")
if failures == 0 {
    print("All report checks passed.")
} else {
    print("\(failures) report check(s) failed.")
    exit(1)
}
