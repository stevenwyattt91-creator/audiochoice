import Foundation

struct CloudUploadProgress: Equatable {
    var bytesSent: Int64
    var totalBytes: Int64

    var fractionCompleted: Double {
        guard totalBytes > 0 else {
            return 0
        }

        return min(
            max(Double(bytesSent) / Double(totalBytes), 0),
            1
        )
    }
}

protocol CloudAudioUploadTransport {
    func upload(
        fileURL: URL,
        request: URLRequest,
        progress: @escaping (CloudUploadProgress) -> Void
    ) async throws -> HTTPURLResponse
}

protocol CloudAudioUploadService {
    func uploadAudio(
        from fileURL: URL,
        authorization: CloudUploadAuthorizationResponse,
        progress: @escaping (CloudUploadProgress) -> Void
    ) async throws
}

enum HTTPCloudAudioUploadServiceError: LocalizedError {
    case authorizationExpired
    case unsuccessfulResponse(Int)

    var errorDescription: String? {
        switch self {
        case .authorizationExpired:
            return "The audio upload authorization has expired."

        case .unsuccessfulResponse(let statusCode):
            return "Audio upload failed with HTTP status \(statusCode)."
        }
    }
}

struct HTTPCloudAudioUploadService: CloudAudioUploadService {
    private let transport: any CloudAudioUploadTransport
    private let currentDate: () -> Date

    init(
        transport: any CloudAudioUploadTransport,
        currentDate: @escaping () -> Date = Date.init
    ) {
        self.transport = transport
        self.currentDate = currentDate
    }

    func uploadAudio(
        from fileURL: URL,
        authorization: CloudUploadAuthorizationResponse,
        progress: @escaping (CloudUploadProgress) -> Void
    ) async throws {
        guard authorization.expiresAt > currentDate() else {
            throw HTTPCloudAudioUploadServiceError
                .authorizationExpired
        }

        var request = URLRequest(
            url: authorization.uploadURL
        )

        request.httpMethod = authorization.method.rawValue

        for (header, value) in authorization.headers {
            request.setValue(
                value,
                forHTTPHeaderField: header
            )
        }

        let response = try await transport.upload(
            fileURL: fileURL,
            request: request,
            progress: progress
        )

        guard (200..<300).contains(response.statusCode) else {
            throw HTTPCloudAudioUploadServiceError
                .unsuccessfulResponse(response.statusCode)
        }
    }
}
