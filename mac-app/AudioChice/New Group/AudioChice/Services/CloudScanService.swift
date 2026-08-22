import Foundation

protocol CloudScanService {
    func requestScan(
        _ request: CloudScanRequest
    ) async throws -> CloudScanResponse

    func requestUploadAuthorization(
        _ request: CloudUploadAuthorizationRequest
    ) async throws -> CloudUploadAuthorizationResponse

    func submitScanJob(
        _ request: CloudScanJobSubmissionRequest
    ) async throws -> CloudScanResponse

    func scanStatus(
        scanID: UUID
    ) async throws -> CloudScanResponse
}

enum CloudScanServiceError: LocalizedError {
    case uploadNotSupported

    var errorDescription: String? {
        switch self {
        case .uploadNotSupported:
            return "This cloud scan service does not support audio uploads."
        }
    }
}

extension CloudScanService {
    func requestUploadAuthorization(
        _ request: CloudUploadAuthorizationRequest
    ) async throws -> CloudUploadAuthorizationResponse {
        throw CloudScanServiceError.uploadNotSupported
    }

    func submitScanJob(
        _ request: CloudScanJobSubmissionRequest
    ) async throws -> CloudScanResponse {
        throw CloudScanServiceError.uploadNotSupported
    }

    func scanStatus(
        scanID: UUID
    ) async throws -> CloudScanResponse {
        throw CloudScanServiceError.uploadNotSupported
    }
}
