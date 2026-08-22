import Foundation

enum AudioChoiceMetadataServiceError: LocalizedError {
    case unableToCreateStorageFolder
    case unableToSaveMetadata

    var errorDescription: String? {
        switch self {
        case .unableToCreateStorageFolder:
            return "AudioChoice could not create its metadata folder."

        case .unableToSaveMetadata:
            return "AudioChoice could not save its metadata database."
        }
    }
}

final class AudioChoiceMetadataService {

    private let fileManager = FileManager.default

    /// Converted files can differ slightly in duration due to encoder padding.
    private let durationTolerance: TimeInterval = 10

    // MARK: - Public Methods

    func contributeMetadata(
        from book: Book
    ) throws {
        guard let identity = book.identity,
              let duration = book.duration,
              duration > 0
        else {
            return
        }

        /*
         Only create a new shared record when the book supplies something
         useful, such as embedded chapters.

         Later this condition can also include filter profiles, corrected
         metadata, improved artwork, and other enhancements.
         */
        guard !book.chapters.isEmpty else {
            return
        }

        var records = try loadAllMetadata()

        if let existingIndex = records.firstIndex(
            where: {
                isExactEditionMatch(
                    metadata: $0,
                    book: book
                )
            }
        ) {
            var existingRecord = records[existingIndex]

            if !book.chapters.isEmpty {
                existingRecord.chapters = book.chapters
            }

            existingRecord.author =
                book.author ?? existingRecord.author

            existingRecord.narrator =
                book.narrator ?? existingRecord.narrator

            existingRecord.sourceFileType =
                book.fileType

            existingRecord.sourceFingerprintSHA256 =
                book.fingerprint?.sha256

            existingRecord.updatedAt = Date()

            records[existingIndex] = existingRecord

        } else {
            let newRecord = AudioChoiceMetadata(
                workTitle: identity.workTitle,
                author: book.author,
                narrator: book.narrator,
                seriesTitle: identity.seriesTitle,
                seriesNumber: identity.seriesNumber,
                editionType: identity.editionType,
                partNumber: identity.partNumber,
                totalParts: identity.totalParts,
                duration: duration,
                sourceFileType: book.fileType,
                sourceFingerprintSHA256:
                    book.fingerprint?.sha256,
                chapters: book.chapters
            )

            records.append(newRecord)
        }

        try saveAllMetadata(records)
    }

    func matchingMetadata(
        for book: Book
    ) throws -> AudioChoiceMetadata? {
        let records = try loadAllMetadata()

        return records
            .filter {
                isExactEditionMatch(
                    metadata: $0,
                    book: book
                )
            }
            .sorted {
                $0.updatedAt > $1.updatedAt
            }
            .first
    }

    @discardableResult
    func applyMatchingMetadata(
        to book: inout Book
    ) throws -> Bool {
        guard let metadata = try matchingMetadata(
            for: book
        ) else {
            return false
        }

        var appliedSomething = false

        // Embedded chapters always take priority.
        if book.chapters.isEmpty,
           !metadata.chapters.isEmpty {
            book.chapters = metadata.chapters
            appliedSomething = true
        }

        if let correctedTitle = cleaned(
            metadata.correctedTitle
        ) {
            book.title = correctedTitle
            appliedSomething = true
        }

        if let correctedAuthor = cleaned(
            metadata.correctedAuthor
        ) {
            book.author = correctedAuthor
            appliedSomething = true
        }

        if let correctedNarrator = cleaned(
            metadata.correctedNarrator
        ) {
            book.narrator = correctedNarrator
            appliedSomething = true
        }

        return appliedSomething
    }

    func loadAllMetadata() throws -> [AudioChoiceMetadata] {
        let fileURL = try metadataFileURL

        guard fileManager.fileExists(
            atPath: fileURL.path
        ) else {
            return []
        }

        let data = try Data(
            contentsOf: fileURL
        )

        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601

        return try decoder.decode(
            [AudioChoiceMetadata].self,
            from: data
        )
    }

    // MARK: - Exact-Edition Matching

    private func isExactEditionMatch(
        metadata: AudioChoiceMetadata,
        book: Book
    ) -> Bool {
        guard let identity = book.identity,
              let duration = book.duration,
              duration > 0
        else {
            return false
        }

        guard normalized(metadata.workTitle) ==
                normalized(identity.workTitle)
        else {
            return false
        }

        guard metadata.editionType ==
                identity.editionType
        else {
            return false
        }

        guard metadata.seriesNumber ==
                identity.seriesNumber
        else {
            return false
        }

        guard metadata.partNumber ==
                identity.partNumber
        else {
            return false
        }

        guard metadata.totalParts ==
                identity.totalParts
        else {
            return false
        }

        guard abs(metadata.duration - duration) <=
                durationTolerance
        else {
            return false
        }

        if !optionalValuesMatch(
            metadata.author,
            book.author
        ) {
            return false
        }

        if !optionalValuesMatch(
            metadata.narrator,
            book.narrator
        ) {
            return false
        }

        if !optionalValuesMatch(
            metadata.seriesTitle,
            identity.seriesTitle
        ) {
            return false
        }

        return true
    }

    private func optionalValuesMatch(
        _ first: String?,
        _ second: String?
    ) -> Bool {
        let normalizedFirst =
            normalizedOptional(first)

        let normalizedSecond =
            normalizedOptional(second)

        /*
         Missing metadata does not automatically reject a match.

         However, when both files provide the value, they must agree.
         */
        guard let normalizedFirst,
              let normalizedSecond
        else {
            return true
        }

        return normalizedFirst == normalizedSecond
    }

    // MARK: - Storage

    private var storageFolderURL: URL {
        get throws {
            let applicationSupportURL =
                try fileManager.url(
                    for: .applicationSupportDirectory,
                    in: .userDomainMask,
                    appropriateFor: nil,
                    create: true
                )

            let audioChoiceURL =
                applicationSupportURL
                    .appendingPathComponent(
                        "AudioChoice",
                        isDirectory: true
                    )

            let metadataURL =
                audioChoiceURL
                    .appendingPathComponent(
                        "Metadata",
                        isDirectory: true
                    )

            if !fileManager.fileExists(
                atPath: metadataURL.path
            ) {
                do {
                    try fileManager.createDirectory(
                        at: metadataURL,
                        withIntermediateDirectories: true
                    )
                } catch {
                    throw AudioChoiceMetadataServiceError
                        .unableToCreateStorageFolder
                }
            }

            return metadataURL
        }
    }

    private var metadataFileURL: URL {
        get throws {
            try storageFolderURL
                .appendingPathComponent(
                    "audiochoice-metadata.json"
                )
        }
    }

    private func saveAllMetadata(
        _ records: [AudioChoiceMetadata]
    ) throws {
        let encoder = JSONEncoder()

        encoder.outputFormatting = [
            .prettyPrinted,
            .sortedKeys
        ]

        encoder.dateEncodingStrategy =
            .iso8601

        do {
            let data = try encoder.encode(records)

            try data.write(
                to: metadataFileURL,
                options: .atomic
            )

        } catch {
            throw AudioChoiceMetadataServiceError
                .unableToSaveMetadata
        }
    }

    // MARK: - Normalization

    private func normalized(
        _ value: String
    ) -> String {
        value
            .lowercased()
            .folding(
                options: [
                    .caseInsensitive,
                    .diacriticInsensitive
                ],
                locale: .current
            )
            .components(
                separatedBy:
                    CharacterSet.alphanumerics
                    .inverted
            )
            .filter {
                !$0.isEmpty
            }
            .joined(separator: " ")
    }

    private func normalizedOptional(
        _ value: String?
    ) -> String? {
        guard let value else {
            return nil
        }

        let normalizedValue = normalized(value)

        return normalizedValue.isEmpty
            ? nil
            : normalizedValue
    }

    private func cleaned(
        _ value: String?
    ) -> String? {
        guard let value else {
            return nil
        }

        let cleanedValue =
            value.trimmingCharacters(
                in: .whitespacesAndNewlines
            )

        return cleanedValue.isEmpty
            ? nil
            : cleanedValue
    }
}
