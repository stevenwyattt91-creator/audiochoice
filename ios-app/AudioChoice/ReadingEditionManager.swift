import Foundation

/// Owns the reading edition for whichever book is open.
///
/// Text extraction, alignment and masking are all expensive enough to be done once and
/// held, rather than recomputed while scrolling. Masks in particular depend on the filter
/// preferences, so they are rebuilt when those change and not on every frame.
@MainActor
final class ReadingEditionManager: ObservableObject {
    static let shared = ReadingEditionManager()

    @Published private(set) var bookID: UUID?
    @Published private(set) var text: String?
    @Published private(set) var paragraphs: [ReaderParagraph] = []
    @Published private(set) var timings: [ReaderTimingRange] = []
    @Published private(set) var displayParagraphs: [ReaderDisplayParagraph] = []
    @Published private(set) var removedPassageCount = 0
    @Published private(set) var syncMessage: String?
    @Published private(set) var isSyncing = false
    @Published var settings: ReaderSettings = .load() {
        didSet {
            guard settings != oldValue else { return }
            settings.save()
        }
    }
    @Published var position = ReaderPosition()

    var hasReadingEdition: Bool { text?.isEmpty == false }

    private init() {}

    // MARK: - Opening

    /// Loads a book's reading edition, preferring the cached extraction.
    ///
    /// Re-unzipping a novel on every open was a multi-hundred-millisecond stall plus a
    /// large transient allocation on Android before it was cached, and iOS is no faster.
    func open(record: LibraryBookRecord) async {
        if bookID == record.id, hasReadingEdition { return }
        reset(for: record.id)
        guard ReaderStore.hasEpub(bookID: record.id) else { return }

        let extracted: String
        if let cached = ReaderStore.cachedText(bookID: record.id), !cached.isEmpty {
            extracted = cached
        } else {
            extracted = await EpubTextReader.read(fileURL: ReaderStore.epubURL(bookID: record.id))
            if !extracted.isEmpty { ReaderStore.saveText(extracted, bookID: record.id) }
        }
        guard !extracted.isEmpty else {
            syncMessage = "That EPUB could not be read."
            return
        }

        await adopt(text: extracted, record: record)

        if let cached = ReaderStore.cachedAlignment(bookID: record.id, matching: extracted) {
            timings = cached
            rebuildMasks(record: record)
        } else {
            // No alignment for this exact text, so ask for one. Read-along is the whole
            // point of pairing an EPUB with an audiobook.
            await sync(record: record)
        }
    }

    /// Attaches a newly picked EPUB.
    func attach(fileURL: URL, record: LibraryBookRecord) async {
        let accessed = fileURL.startAccessingSecurityScopedResource()
        defer { if accessed { fileURL.stopAccessingSecurityScopedResource() } }
        do {
            try ReaderStore.saveEpub(from: fileURL, bookID: record.id)
        } catch {
            syncMessage = "That EPUB could not be saved to this device."
            return
        }
        reset(for: record.id)
        let extracted = await EpubTextReader.read(fileURL: ReaderStore.epubURL(bookID: record.id))
        guard !extracted.isEmpty else {
            ReaderStore.removeEpub(bookID: record.id)
            syncMessage = "That EPUB could not be read."
            return
        }
        ReaderStore.saveText(extracted, bookID: record.id)
        await adopt(text: extracted, record: record)
        await sync(record: record)
    }

    func detach(record: LibraryBookRecord) {
        ReaderStore.removeEpub(bookID: record.id)
        reset(for: nil)
    }

    // MARK: - Alignment

    /// Asks the server to map this text onto the audiobook's timing.
    func sync(record: LibraryBookRecord) async {
        guard let text, !text.isEmpty else { return }
        guard let accountID = record.accountLibraryID else {
            syncMessage = "This audiobook is not linked to your account yet, so read-along cannot be set up."
            return
        }
        isSyncing = true
        syncMessage = "Syncing reading edition…"
        defer { isSyncing = false }

        do {
            let client = try CloudScanClient.configured()
            let response = try await client.createReaderAlignment(bookID: accountID, epubText: text)
            // Only cache a real answer. Caching a failure as "matched, zero ranges" would
            // stop it ever being retried.
            ReaderStore.saveAlignment(response.ranges, bookID: record.id, text: text)
            timings = response.ranges
            rebuildMasks(record: record)
            syncMessage = response.ranges.isEmpty
                ? "This reading edition did not match the audiobook text, so the reader cannot follow along."
                : "Synced \(response.ranges.count) reading sections to the audiobook."
        } catch {
            // Naming the real reason matters: a network failure, an expired session and
            // "this book has no transcript yet" need different actions from the listener.
            syncMessage = Self.syncFailureMessage(error)
        }
    }

    /// One message covering a network failure, an expired session and "this book has no
    /// transcript yet" would be unactionable, and each needs something different from the
    /// listener. The server's own wording is the useful part of a 404 here.
    static func syncFailureMessage(_ error: Error) -> String {
        if case let CloudClientError.server(code, message) = error {
            if code == 401 { return "Your session expired. Sign in again, then tap Re-sync." }
            if let message, !message.isEmpty { return message }
            if code == 400 { return "This reading edition is empty or too large to sync." }
            return "Reading sync failed (\(code))."
        }
        return "Could not reach AudioChoice to sync the reading edition. Check your connection, then tap Re-sync."
    }

    // MARK: - Masking

    /// Rebuilds removal ranges. Called when the text, timings or filter choices change —
    /// never per scroll frame.
    func rebuildMasks(record: LibraryBookRecord?) {
        guard let text, !text.isEmpty else {
            displayParagraphs = []
            removedPassageCount = 0
            return
        }
        let enabled = (record?.scanResult?.events ?? []).filter(IOSContentTaxonomy.shouldSkip)
        let masks = ReaderFilterMasks.build(events: enabled, timings: timings, text: text)
        displayParagraphs = readerDisplayParagraphs(paragraphs, masks: masks)
        removedPassageCount = displayParagraphs.reduce(0) { $0 + $1.removedPassages }
    }

    // MARK: - Position

    func savePosition(paragraphIndex: Int, fraction: Double) {
        guard let bookID else { return }
        let updated = ReaderPosition(
            paragraphIndex: max(paragraphIndex, 0),
            paragraphFraction: min(max(fraction, 0), 1)
        )
        guard updated != position else { return }
        position = updated
        ReaderStore.savePosition(updated, bookID: bookID)
    }

    // MARK: - Internals

    private func adopt(text extracted: String, record: LibraryBookRecord) async {
        text = extracted
        // Parsing a novel is a single pass over roughly a megabyte: cheap, but not free
        // enough to do during a view update.
        paragraphs = await Task.detached(priority: .userInitiated) {
            ReaderParagraphParser.parse(extracted)
        }.value
        position = ReaderStore.position(bookID: record.id)
        rebuildMasks(record: record)
    }

    private func reset(for id: UUID?) {
        bookID = id
        text = nil
        paragraphs = []
        timings = []
        displayParagraphs = []
        removedPassageCount = 0
        syncMessage = nil
        position = ReaderPosition()
    }
}
