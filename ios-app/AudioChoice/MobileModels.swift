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
    /// The title as the file described itself, kept even after the listener renames the
    /// book.
    ///
    /// `book.title` is what the app displays and is editable. Identification has to keep
    /// working from what the file actually said, because a typed-in title would otherwise
    /// start steering which recording this is taken to be. Nil on books imported before
    /// renaming existed, where the displayed title is still the file's own.
    var identityTitle: String? = nil
    /// Whether this book has been finished, either by playing to the end or by the
    /// listener saying so.
    var isFinished: Bool = false
    /// Read from the file's own tags. Part of identifying an edition, since two
    /// readings of the same book share a title, an author and often a runtime.
    var narrator: String? = nil
    /// Identity evidence the server cannot gather for itself, because it only ever
    /// sees decoded audio and never the container tags.
    var editionSignature: EditionSignature? = nil
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

    /// The title to send as identity evidence: the file's own, never a correction.
    var evidenceTitle: String { identityTitle ?? book.title }

    init(
        id: UUID,
        book: MobileBook,
        identityTitle: String? = nil,
        isFinished: Bool = false,
        narrator: String? = nil,
        editionSignature: EditionSignature? = nil,
        localFileName: String?,
        artworkFileName: String?,
        fileSize: Int64,
        importedAt: Date,
        fingerprint: BookFingerprint? = nil,
        scanResult: ScanResult? = nil,
        chapterMarkers: [AudiobookChapter]? = nil,
        pendingScanID: UUID? = nil,
        scanState: String? = nil,
        accountLibraryID: UUID? = nil
    ) {
        self.id = id
        self.book = book
        self.identityTitle = identityTitle
        self.isFinished = isFinished
        self.narrator = narrator
        self.editionSignature = editionSignature
        self.localFileName = localFileName
        self.artworkFileName = artworkFileName
        self.fileSize = fileSize
        self.importedAt = importedAt
        self.fingerprint = fingerprint
        self.scanResult = scanResult
        self.chapterMarkers = chapterMarkers
        self.pendingScanID = pendingScanID
        self.scanState = scanState
        self.accountLibraryID = accountLibraryID
    }

    /// Decodes leniently, because the stored library outlives any one version of this type.
    ///
    /// Swift's synthesised Decodable ignores default values and demands the key, so simply
    /// adding a non-optional property with a default makes every previously saved record
    /// fail to decode. `AudiobookLibraryStore.load` falls back to an empty array on error,
    /// which would have presented an existing listener with an empty library after
    /// updating. Only the identifier and the book itself are genuinely required; anything
    /// else absent falls back.
    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        id = try values.decode(UUID.self, forKey: .id)
        book = try values.decode(MobileBook.self, forKey: .book)
        identityTitle = try values.decodeIfPresent(String.self, forKey: .identityTitle)
        isFinished = try values.decodeIfPresent(Bool.self, forKey: .isFinished) ?? false
        narrator = try values.decodeIfPresent(String.self, forKey: .narrator)
        editionSignature = try values.decodeIfPresent(EditionSignature.self, forKey: .editionSignature)
        localFileName = try values.decodeIfPresent(String.self, forKey: .localFileName)
        artworkFileName = try values.decodeIfPresent(String.self, forKey: .artworkFileName)
        fileSize = try values.decodeIfPresent(Int64.self, forKey: .fileSize) ?? 0
        importedAt = try values.decodeIfPresent(Date.self, forKey: .importedAt) ?? Date()
        fingerprint = try values.decodeIfPresent(BookFingerprint.self, forKey: .fingerprint)
        scanResult = try values.decodeIfPresent(ScanResult.self, forKey: .scanResult)
        chapterMarkers = try values.decodeIfPresent([AudiobookChapter].self, forKey: .chapterMarkers)
        pendingScanID = try values.decodeIfPresent(UUID.self, forKey: .pendingScanID)
        scanState = try values.decodeIfPresent(String.self, forKey: .scanState)
        accountLibraryID = try values.decodeIfPresent(UUID.self, forKey: .accountLibraryID)
    }
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

    @discardableResult
    static func setFinished(_ isFinished: Bool, for bookID: UUID) -> LibraryBookRecord? {
        var records = load()
        guard let index = records.firstIndex(where: { $0.id == bookID }) else { return nil }
        guard records[index].isFinished != isFinished else { return records[index] }
        records[index].isFinished = isFinished
        persist(records)
        return records[index]
    }

    /// Renames a book for display without disturbing what identifies it.
    ///
    /// The file's own title is captured on the first rename, so identification keeps
    /// using it afterwards. Doing it here rather than at import means books added before
    /// renaming existed are covered too.
    @discardableResult
    static func rename(_ title: String, for bookID: UUID) -> LibraryBookRecord? {
        let trimmed = title.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        var records = load()
        guard let index = records.firstIndex(where: { $0.id == bookID }) else { return nil }
        if records[index].identityTitle == nil {
            records[index].identityTitle = records[index].book.title
        }
        records[index].book.title = trimmed
        persist(records)
        return records[index]
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
