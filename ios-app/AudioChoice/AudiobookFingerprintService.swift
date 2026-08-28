import AVFoundation
import CryptoKit
import Foundation
import UniformTypeIdentifiers

enum FingerprintError: LocalizedError {
    case unreadableFile

    var errorDescription: String? { "AudioChoice could not read the selected audiobook." }
}

/// A file's identity: its byte hash plus whatever its own tags say about the edition.
struct InspectedAudiobook {
    let fingerprint: BookFingerprint
    let tags: AudioEditionTags
    /// False when the title had to be guessed from the filename, meaning the edition
    /// is not actually known and should not be presented as fact.
    let titleFromMetadata: Bool
}

struct AudiobookFingerprintService {
    /// Convenience for callers that only need the fingerprint.
    func fingerprint(fileURL: URL) async throws -> BookFingerprint {
        try await inspect(fileURL: fileURL).fingerprint
    }

    func inspect(fileURL: URL) async throws -> InspectedAudiobook {
        try await Task.detached(priority: .userInitiated) {
            let values = try fileURL.resourceValues(forKeys: [.fileSizeKey, .contentTypeKey])
            guard let fileSize = values.fileSize else { throw FingerprintError.unreadableFile }
            let handle = try FileHandle(forReadingFrom: fileURL)
            defer { try? handle.close() }
            var hasher = SHA256()
            while let data = try handle.read(upToCount: 1_048_576), !data.isEmpty {
                try Task.checkCancellation()
                hasher.update(data: data)
            }
            let digest = hasher.finalize().map { String(format: "%02X", $0) }.joined()
            let duration = try? await AVURLAsset(url: fileURL).load(.duration).seconds

            // The container's own tags, which is where the edition is actually named.
            // Previously every one of these fields was sent as nil and the title was
            // taken from the filename, so the server had nothing to identify an
            // edition by beyond a byte hash that any conversion invalidates.
            let tags = Mp4TagReader().read(fileURL: fileURL)
            let taggedTitle = tags.title
            let filenameTitle = AudiobookTitleFormatter.cleanFilename(
                fileURL.deletingPathExtension().lastPathComponent
            )

            return InspectedAudiobook(
                fingerprint: BookFingerprint(
                    version: 1,
                    sha256: digest,
                    fileSize: Int64(fileSize),
                    duration: duration?.isFinite == true ? duration : nil,
                    fileType: values.contentType?.preferredMIMEType ?? "application/octet-stream",
                    workTitle: AudiobookTitleFormatter.format(taggedTitle ?? filenameTitle),
                    author: tags.author,
                    seriesTitle: tags.seriesTitle,
                    seriesNumber: tags.seriesPart,
                    editionType: nil,
                    partNumber: nil,
                    totalParts: nil
                ),
                tags: tags,
                titleFromMetadata: taggedTitle != nil
            )
        }.value
    }
}
