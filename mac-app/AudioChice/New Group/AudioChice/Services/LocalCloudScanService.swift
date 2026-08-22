import Foundation

struct LocalCloudScanService: CloudScanService {
    private let books: [Book]

    init(books: [Book]) {
        self.books = books
    }

    func requestScan(
        _ request: CloudScanRequest
    ) async throws -> CloudScanResponse {
        guard let matchingBook = books.first(
            where: {
                fingerprintsMatch(
                    $0.fingerprint,
                    request.fingerprint
                )
                    && $0.scanResult != nil
            }
        ),
        let result = matchingBook.scanResult
        else {
            return CloudScanResponse(
                status: .uploadRequired
            )
        }

        return CloudScanResponse(
            status: .available,
            result: result
        )
    }

    private func fingerprintsMatch(
        _ storedFingerprint: BookFingerprint?,
        _ requestedFingerprint: BookFingerprint
    ) -> Bool {
        guard let storedFingerprint else {
            return false
        }

        return storedFingerprint.version == requestedFingerprint.version
            && storedFingerprint.sha256 == requestedFingerprint.sha256
            && storedFingerprint.fileSize == requestedFingerprint.fileSize
    }
}
