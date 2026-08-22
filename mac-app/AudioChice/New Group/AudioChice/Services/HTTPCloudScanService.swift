import Foundation

struct CloudScanAPIConfiguration {
    var baseURL: URL

    var scanRequestURL: URL {
        baseURL
            .appendingPathComponent("v1")
            .appendingPathComponent("scans")
            .appendingPathComponent("requests")
    }

    var uploadAuthorizationURL: URL {
        baseURL
            .appendingPathComponent("v1")
            .appendingPathComponent("uploads")
            .appendingPathComponent("authorizations")
    }

    var scanJobsURL: URL {
        baseURL
            .appendingPathComponent("v1")
            .appendingPathComponent("scans")
            .appendingPathComponent("jobs")
    }

    func scanJobURL(scanID: UUID) -> URL {
        scanJobsURL.appendingPathComponent(
            scanID.uuidString
        )
    }
}

protocol CloudScanHTTPTransport {
    func send(
        _ request: URLRequest
    ) async throws -> (Data, HTTPURLResponse)
}

protocol CloudScanAccessTokenProvider {
    func accessToken() async throws -> String
}

enum HTTPCloudScanServiceError: LocalizedError {
    case unsuccessfulResponse(Int)

    var errorDescription: String? {
        switch self {
        case .unsuccessfulResponse(let statusCode):
            return "Cloud scan request failed with HTTP status \(statusCode)."
        }
    }
}

struct HTTPCloudScanService: CloudScanService {
    private let configuration: CloudScanAPIConfiguration
    private let transport: any CloudScanHTTPTransport
    private let tokenProvider: (any CloudScanAccessTokenProvider)?
    private let encoder: JSONEncoder
    private let decoder: JSONDecoder

    init(
        configuration: CloudScanAPIConfiguration,
        transport: any CloudScanHTTPTransport,
        tokenProvider: (any CloudScanAccessTokenProvider)? = nil,
        encoder: JSONEncoder? = nil,
        decoder: JSONDecoder? = nil
    ) {
        self.configuration = configuration
        self.transport = transport
        self.tokenProvider = tokenProvider
        self.encoder = encoder ?? Self.makeEncoder()
        self.decoder = decoder ?? Self.makeDecoder()
    }

    func requestScan(
        _ cloudRequest: CloudScanRequest
    ) async throws -> CloudScanResponse {
        try await send(
            cloudRequest,
            to: configuration.scanRequestURL,
            responseType: CloudScanResponse.self
        )
    }

    func requestUploadAuthorization(
        _ authorizationRequest: CloudUploadAuthorizationRequest
    ) async throws -> CloudUploadAuthorizationResponse {
        try await send(
            authorizationRequest,
            to: configuration.uploadAuthorizationURL,
            responseType: CloudUploadAuthorizationResponse.self
        )
    }

    func submitScanJob(
        _ submissionRequest: CloudScanJobSubmissionRequest
    ) async throws -> CloudScanResponse {
        try await send(
            submissionRequest,
            to: configuration.scanJobsURL,
            responseType: CloudScanResponse.self
        )
    }

    func scanStatus(
        scanID: UUID
    ) async throws -> CloudScanResponse {
        let request = try await makeRequest(
            url: configuration.scanJobURL(scanID: scanID),
            method: "GET"
        )

        return try await perform(
            request,
            responseType: CloudScanResponse.self
        )
    }

    private func send<Request: Encodable, Response: Decodable>(
        _ value: Request,
        to url: URL,
        responseType: Response.Type
    ) async throws -> Response {
        var request = try await makeRequest(
            url: url,
            method: "POST"
        )

        request.httpBody = try encoder.encode(value)

        return try await perform(
            request,
            responseType: responseType
        )
    }

    private func makeRequest(
        url: URL,
        method: String
    ) async throws -> URLRequest {
        var request = URLRequest(url: url)

        request.httpMethod = method
        request.setValue(
            "application/json",
            forHTTPHeaderField: "Content-Type"
        )
        request.setValue(
            "application/json",
            forHTTPHeaderField: "Accept"
        )

        if let tokenProvider {
            let token = try await tokenProvider.accessToken()

            request.setValue(
                "Bearer \(token)",
                forHTTPHeaderField: "Authorization"
            )
        }

        return request
    }

    private func perform<Response: Decodable>(
        _ request: URLRequest,
        responseType: Response.Type
    ) async throws -> Response {

        let (data, response) = try await transport.send(request)

        guard (200..<300).contains(response.statusCode) else {
            throw HTTPCloudScanServiceError
                .unsuccessfulResponse(response.statusCode)
        }

        return try decoder.decode(
            responseType,
            from: data
        )
    }

    private static func makeEncoder() -> JSONEncoder {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        return encoder
    }

    private static func makeDecoder() -> JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return decoder
    }
}
