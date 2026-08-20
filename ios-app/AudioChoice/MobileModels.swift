import Foundation

struct MobileBook: Identifiable, Hashable, Codable {
    var id = UUID()
    var title: String
    var author: String
    var progress: Double
    var timeRemaining: String
    var runtime: String
    var chapters: Int
    var edition: String
}

struct LibraryBookRecord: Identifiable, Codable {
    let id: UUID
    var book: MobileBook
    var localFileName: String?
    var artworkFileName: String?
    var fileSize: Int64
    var importedAt: Date
    var fingerprint: BookFingerprint?
    var scanResult: ScanResult?
    var chapterMarkers: [AudiobookChapter]?
    var pendingScanID: UUID?
    var scanState: String?
    /// Server-side library record. The audio itself stays private to this device.
    var accountLibraryID: UUID? = nil
}

struct AudiobookChapter: Identifiable, Codable, Hashable {
    let id: UUID
    let title: String
    let startTime: Double
    let duration: Double
}

enum AudiobookLibraryStore {
    private static let key = "audiobookLibrary.v1"

    static func load() -> [LibraryBookRecord] {
        guard let data = UserDefaults.standard.data(forKey: key) else { return [] }
        return (try? JSONDecoder().decode([LibraryBookRecord].self, from: data)) ?? []
    }

    static func record(matching fingerprint: BookFingerprint) -> LibraryBookRecord? {
        load().first { $0.fingerprint?.sha256 == fingerprint.sha256 }
    }

    static func upsert(_ record: LibraryBookRecord) {
        var records = load()
        records.removeAll { $0.id == record.id }
        records.insert(record, at: 0)
        persist(records)
    }

    static func update(_ record: LibraryBookRecord) {
        var records = load()
        guard let index = records.firstIndex(where: { $0.id == record.id }) else { return }
        records[index] = record
        persist(records)
    }

    static func attach(result: ScanResult, to id: UUID) {
        var records = load()
        guard let index = records.firstIndex(where: { $0.id == id }) else { return }
        records[index].scanResult = result
        records[index].pendingScanID = nil
        records[index].scanState = CloudScanStatus.completed.rawValue
        persist(records)
    }

    static func setPendingScan(id scanID: UUID, state: CloudScanStatus, for bookID: UUID) {
        var records = load()
        guard let index = records.firstIndex(where: { $0.id == bookID }) else { return }
        records[index].pendingScanID = scanID
        records[index].scanState = state.rawValue
        persist(records)
    }

    static func setScanState(_ state: CloudScanStatus, for bookID: UUID) {
        var records = load()
        guard let index = records.firstIndex(where: { $0.id == bookID }) else { return }
        records[index].scanState = state.rawValue
        if state == .failed { records[index].pendingScanID = nil }
        persist(records)
    }

    static func remove(_ record: LibraryBookRecord) {
        if let name = record.localFileName {
            try? FileManager.default.removeItem(at: AudiobookImportService.audioURL(fileName: name))
        }
        if let name = record.artworkFileName {
            try? FileManager.default.removeItem(at: AudiobookImportService.artworkURL(fileName: name))
        }
        persist(load().filter { $0.id != record.id })
    }

    static var storageBytes: Int64 {
        load().reduce(0) { $0 + $1.fileSize }
    }

    private static func persist(_ records: [LibraryBookRecord]) {
        guard let data = try? JSONEncoder().encode(records) else { return }
        UserDefaults.standard.set(data, forKey: key)
    }
}

enum MobileSamples {
    static let featured = MobileBook(
        title: "The Way of Kings",
        author: "Brandon Sanderson",
        progress: 0.42,
        timeRemaining: "18h 47m left",
        runtime: "45h 12m",
        chapters: 136,
        edition: "GraphicAudio Edition"
    )

    static let recent = [
        MobileBook(title: "Mistborn", author: "Brandon Sanderson", progress: 0, timeRemaining: "", runtime: "24h", chapters: 82, edition: "Standard"),
        MobileBook(title: "The Count of Monte Cristo", author: "Alexandre Dumas", progress: 0.65, timeRemaining: "", runtime: "47h", chapters: 117, edition: "Standard"),
        MobileBook(title: "Fourth Wing", author: "Rebecca Yarros", progress: 0, timeRemaining: "", runtime: "21h", chapters: 39, edition: "Standard")
    ]
}

struct ScanStep: Identifiable {
    let id = UUID()
    var icon: String
    var title: String
    var status: String
    var isComplete: Bool
    var isActive: Bool
}

struct FilterOption: Identifiable {
    let id = UUID()
    var icon: String
    var title: String
    var detail: String
    var isEnabled: Bool
}
