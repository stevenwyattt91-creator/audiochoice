import Foundation

enum LibraryStorageError: LocalizedError {
    case unableToCreateLibraryFolder
    case unableToCopyAudiobook
    case unableToSaveLibrary

    var errorDescription: String? {
        switch self {
        case .unableToCreateLibraryFolder:
            return "AudioChoice could not create its library folder."

        case .unableToCopyAudiobook:
            return "AudioChoice could not copy the audiobook into its library."

        case .unableToSaveLibrary:
            return "AudioChoice could not save the audiobook library."
        }
    }
}

final class LibraryStorageService {

    private let fileManager = FileManager.default

    private var applicationSupportURL: URL {
        get throws {
            let baseURL = try fileManager.url(
                for: .applicationSupportDirectory,
                in: .userDomainMask,
                appropriateFor: nil,
                create: true
            )

            let audioChoiceURL = baseURL
                .appendingPathComponent(
                    "AudioChoice",
                    isDirectory: true
                )

            if !fileManager.fileExists(
                atPath: audioChoiceURL.path
            ) {
                do {
                    try fileManager.createDirectory(
                        at: audioChoiceURL,
                        withIntermediateDirectories: true
                    )
                } catch {
                    throw LibraryStorageError
                        .unableToCreateLibraryFolder
                }
            }

            return audioChoiceURL
        }
    }

    private var audiobookFolderURL: URL {
        get throws {
            let folderURL = try applicationSupportURL
                .appendingPathComponent(
                    "Audiobooks",
                    isDirectory: true
                )

            if !fileManager.fileExists(
                atPath: folderURL.path
            ) {
                try fileManager.createDirectory(
                    at: folderURL,
                    withIntermediateDirectories: true
                )
            }

            return folderURL
        }
    }

    private var libraryFileURL: URL {
        get throws {
            try applicationSupportURL
                .appendingPathComponent("library.json")
        }
    }

    func copyAudiobookToLibrary(
        from sourceURL: URL,
        bookID: UUID
    ) throws -> URL {
        let extensionName = sourceURL.pathExtension

        let destinationURL = try audiobookFolderURL
            .appendingPathComponent(
                bookID.uuidString
            )
            .appendingPathExtension(extensionName)

        if fileManager.fileExists(
            atPath: destinationURL.path
        ) {
            try fileManager.removeItem(
                at: destinationURL
            )
        }

        do {
            try fileManager.copyItem(
                at: sourceURL,
                to: destinationURL
            )

            return destinationURL
        } catch {
            throw LibraryStorageError
                .unableToCopyAudiobook
        }
    }

    func saveBooks(
        _ books: [Book]
    ) throws {
        let encoder = JSONEncoder()

        encoder.outputFormatting = [
            .prettyPrinted,
            .sortedKeys
        ]

        do {
            let data = try encoder.encode(books)

            try data.write(
                to: libraryFileURL,
                options: .atomic
            )
        } catch {
            throw LibraryStorageError
                .unableToSaveLibrary
        }
    }

    func loadBooks() throws -> [Book] {
        let fileURL = try libraryFileURL

        guard fileManager.fileExists(
            atPath: fileURL.path
        ) else {
            return []
        }

        let data = try Data(
            contentsOf: fileURL
        )

        return try JSONDecoder().decode(
            [Book].self,
            from: data
        )
    }
    func deleteAudiobookFiles(
        for book: Book
    ) throws {
        let fileURLs = [
            book.originalFileURL,
            book.convertedFileURL
        ]
        .compactMap { $0 }

        for fileURL in fileURLs {
            guard fileManager.fileExists(
                atPath: fileURL.path
            ) else {
                continue
            }

            try fileManager.removeItem(
                at: fileURL
            )
        }
    }
}
