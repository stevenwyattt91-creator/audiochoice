import Foundation

final class TranscriptStorageService {

    private let fileManager = FileManager.default

    private var transcriptsFolder: URL {

        let applicationSupport =
            fileManager.urls(
                for: .applicationSupportDirectory,
                in: .userDomainMask
            ).first!

        let folder =
            applicationSupport
                .appendingPathComponent("AudioChoice")
                .appendingPathComponent("Transcripts")

        if !fileManager.fileExists(atPath: folder.path) {
            try? fileManager.createDirectory(
                at: folder,
                withIntermediateDirectories: true
            )
        }

        return folder
    }

    func transcriptURL(
        for bookID: UUID
    ) -> URL {

        transcriptsFolder
            .appendingPathComponent(bookID.uuidString)
            .appendingPathExtension("json")
    }

    func save(
        _ transcript: Transcript,
        for bookID: UUID
    ) throws {

        let data =
            try JSONEncoder().encode(transcript)

        try data.write(
            to: transcriptURL(for: bookID)
        )
    }

    func load(
        for bookID: UUID
    ) throws -> Transcript? {

        let url =
            transcriptURL(for: bookID)

        guard fileManager.fileExists(
            atPath: url.path
        ) else {
            return nil
        }

        let data =
            try Data(contentsOf: url)

        return try JSONDecoder()
            .decode(
                Transcript.self,
                from: data
            )
    }

    func delete(
        for bookID: UUID
    ) throws {

        let url =
            transcriptURL(for: bookID)

        guard fileManager.fileExists(
            atPath: url.path
        ) else {
            return
        }

        try fileManager.removeItem(at: url)
    }
}
