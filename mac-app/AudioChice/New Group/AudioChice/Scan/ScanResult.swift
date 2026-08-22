import Foundation

struct ScanResult: Codable {

    var events: [ScanEvent]

    var scanDate: Date

    var scannerVersion: String

    init(
        events: [ScanEvent] = [],
        scanDate: Date = Date(),
        scannerVersion: String = "1.0"
    ) {
        self.events = events
        self.scanDate = scanDate
        self.scannerVersion = scannerVersion
    }
}
