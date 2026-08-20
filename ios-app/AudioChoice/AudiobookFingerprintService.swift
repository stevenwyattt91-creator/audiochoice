import AVFoundation
import CryptoKit
import Foundation
import UniformTypeIdentifiers

enum FingerprintError: LocalizedError {
    case unreadableFile

    var errorDescription: String? { "AudioChoice could not read the selected audiobook." }
}

struct AudiobookFingerprintService {
    func fingerprint(fileURL: URL) async throws -> BookFingerprint {
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
            return BookFingerprint(
                version: 1,
                sha256: digest,
                fileSize: Int64(fileSize),
                duration: duration?.isFinite == true ? duration : nil,
                fileType: values.contentType?.preferredMIMEType ?? "application/octet-stream",
                workTitle: fileURL.deletingPathExtension().lastPathComponent,
                author: nil,
                seriesTitle: nil,
                seriesNumber: nil,
                editionType: nil,
                partNumber: nil,
                totalParts: nil
            )
        }.value
    }
}
