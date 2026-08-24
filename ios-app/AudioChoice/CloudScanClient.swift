import Foundation

enum CloudClientError: LocalizedError {
    case invalidConfiguration
    case server(Int, String?)
    case invalidResponse
    case uploadAuthorizationExpired
    case scanFailed
    case missingScanID
    case missingResult
    case timedOut

    var errorDescription: String? {
        switch self {
        case .invalidConfiguration:
            "Add the AudioChoice server address and access token in Profile before scanning."
        case let .server(code, message):
            message ?? "The AudioChoice server returned error \(code)."
        case .invalidResponse:
            "The AudioChoice server returned an unreadable response."
        case .uploadAuthorizationExpired:
            "The private upload permission expired. Please try again."
        case .scanFailed:
            "AudioChoice could not complete this scan."
        case .missingScanID:
            "The server did not return a scan identifier."
        case .missingResult:
            "The completed scan did not contain filter results."
        case .timedOut:
            "The scan is still taking longer than expected. You can try again later."
        }
    }
}

struct CloudScanClient {
    let baseURL: URL
    let accessToken: String
    var session: URLSession = CloudScanClient.uploadSession

    private static let uploadSession: URLSession = {
        let configuration = URLSessionConfiguration.default
        configuration.waitsForConnectivity = true
        configuration.timeoutIntervalForRequest = 30 * 60
        configuration.timeoutIntervalForResource = 2 * 60 * 60
        return URLSession(configuration: configuration)
    }()

    static func configured() throws -> CloudScanClient {
        let defaults = UserDefaults.standard
        let address = defaults.string(forKey: "cloudBaseURL")?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let token = CloudCredentialStore.loadToken().trimmingCharacters(in: .whitespacesAndNewlines)
        guard let url = URL(string: address), !address.isEmpty, !token.isEmpty else {
            throw CloudClientError.invalidConfiguration
        }
        return CloudScanClient(baseURL: url, accessToken: token)
    }

    func requestScan(_ value: CloudScanRequest) async throws -> CloudScanResponse {
        try await post(value, path: "v1/scans/requests")
    }

    func authorizeUpload(_ value: CloudUploadAuthorizationRequest) async throws -> CloudUploadAuthorizationResponse {
        try await post(value, path: "v1/uploads/authorizations")
    }

    func submitJob(_ value: CloudScanJobSubmissionRequest) async throws -> CloudScanResponse {
        try await post(value, path: "v1/scans/jobs")
    }

    func job(scanID: UUID) async throws -> CloudScanResponse {
        var request = URLRequest(url: endpoint("v1/scans/jobs/\(scanID.uuidString)"))
        request.httpMethod = "GET"
        addAPIHeaders(to: &request)
        return try await response(for: request)
    }

    func library() async throws -> [AccountLibraryBook] {
        try await get(path: "v1/library")
    }

    func saveLibraryBook(_ value: LibraryBookUpsertRequest) async throws -> AccountLibraryBook {
        try await put(value, path: "v1/library")
    }

    func saveProgress(bookID: UUID, position: Double, isFinished: Bool = false) async throws -> AccountLibraryBook {
        try await put(
            PlaybackProgressRequest(positionSeconds: position, isFinished: isFinished),
            path: "v1/library/\(bookID.uuidString)/progress")
    }

    func exploreBooks() async throws -> [ExploreCatalogBook] {
        try await get(path: "v1/explore")
    }

    func sendSupportMessage(subject: String, message: String) async throws -> SupportMessageResponse {
        try await post(SupportMessageRequest(subject: subject, message: message), path: "v1/support/messages")
    }

    func coverURL(for book: ExploreCatalogBook) -> URL? {
        guard let value = book.coverImageURL, !value.isEmpty else { return nil }
        if let absolute = URL(string: value), absolute.scheme != nil {
            return absolute
        }
        return endpoint(value)
    }

    func upload(fileURL: URL, authorization: CloudUploadAuthorizationResponse) async throws {
        guard authorization.expiresAt > Date() else {
            throw CloudClientError.uploadAuthorizationExpired
        }
        var request = URLRequest(url: authorization.uploadURL)
        request.httpMethod = authorization.method
        for (name, value) in authorization.headers {
            request.setValue(value, forHTTPHeaderField: name)
        }
        let (_, response) = try await session.upload(for: request, fromFile: fileURL)
        try validate(response: response, data: nil)
    }

    private func post<Input: Encodable, Output: Decodable>(_ value: Input, path: String) async throws -> Output {
        var request = URLRequest(url: endpoint(path))
        request.httpMethod = "POST"
        addAPIHeaders(to: &request)
        request.httpBody = try Self.encoder.encode(value)
        return try await response(for: request)
    }

    private func put<Input: Encodable, Output: Decodable>(_ value: Input, path: String) async throws -> Output {
        var request = URLRequest(url: endpoint(path))
        request.httpMethod = "PUT"
        addAPIHeaders(to: &request)
        request.httpBody = try Self.encoder.encode(value)
        return try await response(for: request)
    }

    private func get<Output: Decodable>(path: String) async throws -> Output {
        var request = URLRequest(url: endpoint(path))
        request.httpMethod = "GET"
        addAPIHeaders(to: &request)
        return try await response(for: request)
    }

    private func response<Output: Decodable>(for request: URLRequest) async throws -> Output {
        let (data, response) = try await session.data(for: request)
        try validate(response: response, data: data)
        do { return try Self.decoder.decode(Output.self, from: data) }
        catch { throw CloudClientError.invalidResponse }
    }

    private func validate(response: URLResponse, data: Data?) throws {
        guard let http = response as? HTTPURLResponse else { throw CloudClientError.invalidResponse }
        guard (200..<300).contains(http.statusCode) else {
            let message = data.flatMap { try? JSONDecoder().decode(ServerError.self, from: $0).error }
            throw CloudClientError.server(http.statusCode, message)
        }
    }

    private func addAPIHeaders(to request: inout URLRequest) {
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("ios-beta", forHTTPHeaderField: "X-AudioChoice-Scan-Channel")
    }

    private func endpoint(_ path: String) -> URL {
        path.split(separator: "/").reduce(baseURL) { $0.appendingPathComponent(String($1)) }
    }

    private struct ServerError: Decodable { let error: String }

    private static let encoder: JSONEncoder = {
        let value = JSONEncoder()
        value.dateEncodingStrategy = .iso8601
        return value
    }()

    private static let decoder: JSONDecoder = {
        let value = JSONDecoder()
        value.dateDecodingStrategy = .iso8601
        return value
    }()
}
