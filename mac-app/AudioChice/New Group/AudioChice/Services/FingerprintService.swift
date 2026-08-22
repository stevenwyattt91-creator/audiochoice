import CryptoKit
import Foundation

enum FingerprintServiceError: LocalizedError {
    case unableToReadFile
    case unableToReadFileSize

    var errorDescription: String? {
        switch self {
        case .unableToReadFile:
            return "AudioChoice could not read the audiobook file."

        case .unableToReadFileSize:
            return "AudioChoice could not determine the audiobook file size."
        }
    }
}

final class FingerprintService {

    func createFingerprint(
        for book: Book
    ) async throws -> BookFingerprint {

        let fileURL = book.convertedFileURL
            ?? book.originalFileURL

        let sha256 = try await calculateSHA256(
            for: fileURL
        )

        let fileSize = try readFileSize(
            from: fileURL
        )

        return BookFingerprint(
            sha256: sha256,
            fileSize: fileSize,
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
    }

    private func calculateSHA256(
        for url: URL
    ) async throws -> String {

        try await Task.detached(
            priority: .userInitiated
        ) {
            guard let inputStream = InputStream(
                url: url
            ) else {
                throw FingerprintServiceError
                    .unableToReadFile
            }

            inputStream.open()

            defer {
                inputStream.close()
            }

            var hasher = SHA256()
            let bufferSize = 1_048_576
            let buffer = UnsafeMutablePointer<UInt8>
                .allocate(capacity: bufferSize)

            defer {
                buffer.deallocate()
            }

            while inputStream.hasBytesAvailable {
                let bytesRead = inputStream.read(
                    buffer,
                    maxLength: bufferSize
                )

                if bytesRead < 0 {
                    throw inputStream.streamError
                        ?? FingerprintServiceError
                            .unableToReadFile
                }

                if bytesRead == 0 {
                    break
                }

                let data = Data(
                    bytes: buffer,
                    count: bytesRead
                )

                hasher.update(data: data)
            }

            let digest = hasher.finalize()

            return digest.map {
                String(
                    format: "%02x",
                    $0
                )
            }
            .joined()
        }
        .value
    }

    private func readFileSize(
        from url: URL
    ) throws -> Int64 {

        let values = try url.resourceValues(
            forKeys: [
                .fileSizeKey
            ]
        )

        guard let fileSize = values.fileSize else {
            throw FingerprintServiceError
                .unableToReadFileSize
        }

        return Int64(fileSize)
    }
}
