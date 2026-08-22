import Combine
import Foundation

enum AddBookResult {
    case added(UUID)
    case duplicate(UUID)
    case failed(String)
}

@MainActor
final class LibraryManager: ObservableObject {

    @Published private(set) var books: [Book] = []

    private let metadataService = MetadataService()
    private let identityService = BookIdentityService()
    private let fingerprintService = FingerprintService()
    private let storageService = LibraryStorageService()
    private let audioChoiceMetadataService =
        AudioChoiceMetadataService()
    private let contentScanner = ContentScanner()
    private let cloudScanService: (any CloudScanService)?

    init(
        cloudScanService: (any CloudScanService)? = nil
    ) {
        self.cloudScanService = cloudScanService
        loadSavedLibrary()
    }

    // MARK: - Import

    func addBook(url: URL) async -> AddBookResult {
        let hasSecurityAccess =
            url.startAccessingSecurityScopedResource()

        defer {
            if hasSecurityAccess {
                url.stopAccessingSecurityScopedResource()
            }
        }

        var book = Book(
            title: fallbackTitle(from: url),
            originalFileURL: url,
            fileType: url.pathExtension.lowercased()
        )

        let initialFingerprint: BookFingerprint

        do {
            initialFingerprint =
                try await fingerprintService
                    .createFingerprint(for: book)

        } catch {
            let message =
                "Fingerprint creation failed: \(error.localizedDescription)"

            print(message)
            return .failed(message)
        }

        if let existingBook = books.first(
            where: {
                $0.fingerprint?.sha256 ==
                    initialFingerprint.sha256
            }
        ) {
            return .duplicate(existingBook.id)
        }

        do {
            let storedURL =
                try storageService.copyAudiobookToLibrary(
                    from: url,
                    bookID: book.id
                )

            book.originalFileURL = storedURL

        } catch {
            let message =
                "Audiobook copy failed: \(error.localizedDescription)"

            print(message)
            return .failed(message)
        }

        do {
            let metadata =
                try await metadataService.extractMetadata(
                    from: book.originalFileURL
                )

            if let title = metadata.title,
               !title.isEmpty {
                book.title = title
            }

            if let author = metadata.author,
               !author.isEmpty {
                book.author = author
            }

            if let narrator = metadata.narrator,
               !narrator.isEmpty {
                book.narrator = narrator
            }

            book.duration = metadata.duration
            book.coverArtData = metadata.coverArtData
            book.chapters = metadata.chapters

        } catch {
            print(
                "Metadata extraction failed: \(error.localizedDescription)"
            )
        }

        let hadEmbeddedChapters =
            !book.chapters.isEmpty

        book.identity = identityService.identify(
            title: book.title,
            author: book.author
        )

        if book.chapters.isEmpty {
            do {
                let appliedMetadata =
                    try audioChoiceMetadataService
                        .applyMatchingMetadata(
                            to: &book
                        )

                if appliedMetadata {
                    book.identity =
                        identityService.identify(
                            title: book.title,
                            author: book.author
                        )
                }

            } catch {
                print(
                    "AudioChoice metadata lookup failed: \(error.localizedDescription)"
                )
            }
        }

        book.fingerprint = BookFingerprint(
            version: initialFingerprint.version,
            sha256: initialFingerprint.sha256,
            fileSize: initialFingerprint.fileSize,
            duration: book.duration,
            fileType: book.fileType,
            workTitle: book.identity?.workTitle,
            author: book.author,
            seriesTitle: book.identity?.seriesTitle,
            seriesNumber: book.identity?.seriesNumber,
            editionType: book.identity?.editionType,
            partNumber: book.identity?.partNumber,
            totalParts: book.identity?.totalParts
        )

        if hadEmbeddedChapters {
            do {
                try audioChoiceMetadataService
                    .contributeMetadata(from: book)

            } catch {
                print(
                    "AudioChoice metadata contribution failed: \(error.localizedDescription)"
                )
            }
        }

        book.scanStatus = .scanning
        book.scanResult = await scanResult(for: book)
        book.scanStatus = .completed

        books.append(book)
        saveLibrary()

        return .added(book.id)
    }

    private func scanResult(
        for book: Book
    ) async -> ScanResult {
        guard let fingerprint = book.fingerprint else {
            return await contentScanner.scan(book: book)
        }

        let service: any CloudScanService =
            cloudScanService
            ?? LocalCloudScanService(books: books)

        let request = CloudScanRequest(
            fingerprint: fingerprint
        )

        do {
            let response = try await service
                .requestScan(request)

            if response.status == .available,
               let result = response.result {
                return result
            }
        } catch {
            print(
                "Cloud scan lookup failed: \(error.localizedDescription)"
            )
        }

        return await contentScanner.scan(book: book)
    }

    // MARK: - Playback Progress

    func updatePlayback(
        bookID: UUID,
        currentPosition: TimeInterval,
        playbackSpeed: Double
    ) {
        guard let index = books.firstIndex(
            where: { $0.id == bookID }
        ) else {
            return
        }

        books[index].currentPosition =
            currentPosition

        books[index].playbackSpeed =
            playbackSpeed

        books[index].lastPlayed =
            Date()

        if let duration = books[index].duration {
            books[index].isFinished =
                currentPosition >= duration - 5
        }

        saveLibrary()
    }

    func markBookFinished(
        bookID: UUID
    ) {
        guard let index = books.firstIndex(
            where: { $0.id == bookID }
        ) else {
            return
        }

        books[index].isFinished = true

        if let duration = books[index].duration {
            books[index].currentPosition =
                duration
        }

        books[index].lastPlayed =
            Date()

        saveLibrary()
    }

    // MARK: - Bookmarks

    @discardableResult
    func addBookmark(
        bookID: UUID,
        position: TimeInterval,
        title: String,
        note: String? = nil
    ) -> UUID? {
        guard let bookIndex = books.firstIndex(
            where: { $0.id == bookID }
        ) else {
            return nil
        }

        let safePosition = min(
            max(position, 0),
            books[bookIndex].duration ?? position
        )

        let cleanedTitle =
            title.trimmingCharacters(
                in: .whitespacesAndNewlines
            )

        let cleanedNote =
            cleanedOptionalText(note)

        let bookmark = Bookmark(
            title: cleanedTitle.isEmpty
                ? defaultBookmarkTitle(
                    for: books[bookIndex],
                    position: safePosition
                )
                : cleanedTitle,
            position: safePosition,
            note: cleanedNote
        )

        books[bookIndex].bookmarks.append(bookmark)

        books[bookIndex].bookmarks.sort {
            $0.position < $1.position
        }

        saveLibrary()

        return bookmark.id
    }

    func updateBookmark(
        bookID: UUID,
        bookmarkID: UUID,
        title: String,
        note: String?
    ) -> Bool {
        guard let bookIndex = books.firstIndex(
            where: { $0.id == bookID }
        ),
        let bookmarkIndex =
            books[bookIndex].bookmarks.firstIndex(
                where: { $0.id == bookmarkID }
            )
        else {
            return false
        }

        let cleanedTitle =
            title.trimmingCharacters(
                in: .whitespacesAndNewlines
            )

        books[bookIndex]
            .bookmarks[bookmarkIndex]
            .title =
                cleanedTitle.isEmpty
                ? defaultBookmarkTitle(
                    for: books[bookIndex],
                    position:
                        books[bookIndex]
                            .bookmarks[bookmarkIndex]
                            .position
                )
                : cleanedTitle

        books[bookIndex]
            .bookmarks[bookmarkIndex]
            .note =
                cleanedOptionalText(note)

        saveLibrary()
        return true
    }

    func deleteBookmark(
        bookID: UUID,
        bookmarkID: UUID
    ) -> Bool {
        guard let bookIndex = books.firstIndex(
            where: { $0.id == bookID }
        ),
        let bookmarkIndex =
            books[bookIndex].bookmarks.firstIndex(
                where: { $0.id == bookmarkID }
            )
        else {
            return false
        }

        books[bookIndex].bookmarks.remove(
            at: bookmarkIndex
        )

        saveLibrary()
        return true
    }

    func bookmarks(
        for bookID: UUID
    ) -> [Bookmark] {
        guard let book = books.first(
            where: { $0.id == bookID }
        ) else {
            return []
        }

        return book.bookmarks.sorted {
            $0.position < $1.position
        }
    }

    private func defaultBookmarkTitle(
        for book: Book,
        position: TimeInterval
    ) -> String {
        let currentChapter =
            book.chapters.last {
                $0.startTime <= position
            }

        if let chapterTitle =
            currentChapter?.title,
           !chapterTitle.isEmpty {
            return chapterTitle
        }

        return "Bookmark at \(formattedBookmarkTime(position))"
    }

    private func formattedBookmarkTime(
        _ position: TimeInterval
    ) -> String {
        let totalSeconds =
            max(0, Int(position))

        let hours =
            totalSeconds / 3600

        let minutes =
            (totalSeconds % 3600) / 60

        let seconds =
            totalSeconds % 60

        if hours > 0 {
            return String(
                format: "%d:%02d:%02d",
                hours,
                minutes,
                seconds
            )
        }

        return String(
            format: "%d:%02d",
            minutes,
            seconds
        )
    }

    private func cleanedOptionalText(
        _ value: String?
    ) -> String? {
        guard let value else {
            return nil
        }

        let cleaned =
            value.trimmingCharacters(
                in: .whitespacesAndNewlines
            )

        return cleaned.isEmpty
            ? nil
            : cleaned
    }

    // MARK: - Filter Profiles

    func updateFilterProfile(
        bookID: UUID,
        profile: FilterProfile
    ) {
        guard let index = books.firstIndex(where: { $0.id == bookID }) else {
            return
        }

        books[index].filterProfile = profile
        saveLibrary()
    }

    private func loadSavedLibrary() {
        do {
            books = try storageService.loadBooks()

        } catch {
            print(
                "Library load failed: \(error.localizedDescription)"
            )

            books = []
        }
    }

    private func saveLibrary() {
        do {
            try storageService.saveBooks(books)

        } catch {
            print(
                "Library save failed: \(error.localizedDescription)"
            )
        }
    }

    // MARK: - Title Cleanup

    private func fallbackTitle(
        from url: URL
    ) -> String {
        let filename = url
            .deletingPathExtension()
            .lastPathComponent

        var cleanedTitle = filename
            .replacingOccurrences(
                of: "_",
                with: " "
            )
            .replacingOccurrences(
                of: "-",
                with: " "
            )

        while cleanedTitle.contains("  ") {
            cleanedTitle =
                cleanedTitle.replacingOccurrences(
                    of: "  ",
                    with: " "
                )
        }

        return cleanedTitle.trimmingCharacters(
            in: .whitespacesAndNewlines
        )
    }

    // MARK: - Book Removal

    func removeBook(
        id: UUID
    ) -> Bool {
        guard let index = books.firstIndex(
            where: { $0.id == id }
        ) else {
            return false
        }

        let book = books[index]

        do {
            try storageService
                .deleteAudiobookFiles(
                    for: book
                )

            books.remove(at: index)
            saveLibrary()

            return true

        } catch {
            print(
                "Book deletion failed: \(error.localizedDescription)"
            )

            return false
        }
    }
}
