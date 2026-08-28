import CryptoKit
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

/// The computer-to-phone handoff uses the same one-time transfer contract as Android.
/// The transfer itself is authenticated and the server deletes it after receipt.
struct CompanionTransferClaim: Decodable {
    let transferID: UUID
    let fileName: String
    let contentType: String
    let fileSize: Int64
    let sha256: String
    let downloadURL: URL
    let expiresAt: Date
}

extension CloudScanClient {
    func claimCompanionTransfer(id: UUID, code: String) async throws -> CompanionTransferClaim {
        var url = endpoint("v1/companion/transfers/\(id.uuidString)/claim")
        var components = URLComponents(url: url, resolvingAgainstBaseURL: false)
        components?.queryItems = [URLQueryItem(name: "code", value: code)]
        url = components?.url ?? url
        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        addAPIHeaders(to: &request)
        return try await response(for: request)
    }

    func markCompanionTransferReceived(id: UUID) async throws {
        var request = URLRequest(url: endpoint("v1/companion/transfers/\(id.uuidString)/received"))
        request.httpMethod = "POST"
        addAPIHeaders(to: &request)
        let (data, response) = try await session.data(for: request)
        try validate(response: response, data: data)
    }

    func downloadCompanionTransfer(_ claim: CompanionTransferClaim, to destination: URL) async throws {
        let (data, response) = try await session.data(from: claim.downloadURL)
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            throw CloudClientError.server((response as? HTTPURLResponse)?.statusCode ?? 0, "The transfer download could not be opened.")
        }
        guard Int64(data.count) == claim.fileSize else { throw CloudClientError.invalidResponse }
        let digest = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
        guard digest.caseInsensitiveCompare(claim.sha256) == .orderedSame else {
            throw CloudClientError.invalidResponse
        }
        try data.write(to: destination, options: .atomic)
    }
}

@MainActor
final class CompanionTransferCoordinator: ObservableObject {
    static let shared = CompanionTransferCoordinator()
    @Published private(set) var pendingURL: URL?
    private init() {}

    func receive(_ url: URL) {
        guard ["audiochoice", "audiochoice-beta"].contains(url.scheme?.lowercased() ?? ""),
              url.host?.lowercased() == "transfer" else { return }
        pendingURL = url
    }

    func clear() { pendingURL = nil }
}

struct CloudScanClient {
    let baseURL: URL
    let accessToken: String
    var session: URLSession = .shared

    static func configured() throws -> CloudScanClient {
        let defaults = UserDefaults.standard
        let savedAddress = defaults.string(forKey: "cloudBaseURL")?
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let bundledAddress = (Bundle.main.object(forInfoDictionaryKey: "AudioChoiceAPIBaseURL") as? String)?
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        // Keep every account-backed feature on the same shipped API as login.
        // A saved address is only a fallback for local development builds.
        let address = bundledAddress.isEmpty ? savedAddress : bundledAddress
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

    func completeUpload(uploadID: UUID) async throws {
        var request = URLRequest(url: endpoint("v1/uploads/\(uploadID.uuidString)/complete"))
        request.httpMethod = "POST"
        addAPIHeaders(to: &request)
        let (data, response) = try await session.data(for: request)
        try validate(response: response, data: data)
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

    /// This book's filter choices as stored for the account, or nil if none are stored.
    ///
    /// A book nobody has adjusted has no server record, and the endpoint answers 404 for
    /// it. That is an ordinary outcome rather than a failure, so it comes back as nil
    /// and the caller keeps whatever this device already had.
    func bookFilterSettings(bookID: UUID) async throws -> RemoteBookFilterSettings? {
        do {
            return try await get(path: "v1/library/\(bookID.uuidString)/filter-settings")
        } catch CloudClientError.server(404, _) {
            return nil
        }
    }

    func saveBookFilterSettings(
        bookID: UUID,
        _ value: BookFilterSettingsUpsertRequest
    ) async throws -> RemoteBookFilterSettings {
        try await put(value, path: "v1/library/\(bookID.uuidString)/filter-settings")
    }

    /// Sends the reading edition's text up and gets timing ranges back.
    ///
    /// The EPUB text is used in memory to build the map and is never persisted server
    /// side, and the response carries only offsets and times -- never transcript text.
    /// That is what lets read-along work without the private transcript ever reaching a
    /// device.
    func createReaderAlignment(bookID: UUID, epubText: String) async throws -> ReaderAlignmentResponse {
        try await post(
            ReaderAlignmentRequest(libraryBookID: bookID, epubText: epubText),
            path: "v1/reader/alignments"
        )
    }

    func exploreBooks() async throws -> [ExploreCatalogBook] {
        try await get(path: "v1/explore")
    }

    func sendSupportMessage(subject: String, message: String) async throws -> SupportMessageResponse {
        try await post(SupportMessageRequest(subject: subject, message: message), path: "v1/support/messages")
    }

    func coverURL(for book: ExploreCatalogBook) -> URL? {
        coverURL(for: book.coverImageURL)
    }

    func coverURL(for value: String?) -> URL? {
        guard let value, !value.isEmpty else { return nil }
        if let absolute = URL(string: value), absolute.scheme != nil {
            return absolute
        }
        return endpoint(value)
    }

    func coverImageData(from url: URL) async throws -> Data {
        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        addAPIHeaders(to: &request)
        request.setValue("image/*", forHTTPHeaderField: "Accept")
        let (data, response) = try await session.data(for: request)
        try validate(response: response, data: data)
        guard !data.isEmpty else { throw CloudClientError.invalidResponse }
        return data
    }

    func uploadExploreCover(_ data: Data, catalogID: String) async throws {
        var request = URLRequest(url: endpoint("v1/explore/\(catalogID)/cover"))
        request.httpMethod = "PUT"
        request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("ios-beta", forHTTPHeaderField: "X-AudioChoice-Scan-Channel")
        request.setValue("image/jpeg", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.httpBody = data
        let (responseData, response) = try await session.data(for: request)
        try validate(response: response, data: responseData)
    }

    func upload(
        fileURL: URL,
        authorization: CloudUploadAuthorizationResponse,
        onProgress: @escaping (Double) -> Void = { _ in }
    ) async throws {
        guard authorization.expiresAt > Date() else {
            throw CloudClientError.uploadAuthorizationExpired
        }
        var request = URLRequest(url: authorization.uploadURL)
        request.httpMethod = authorization.method
        for (name, value) in authorization.headers {
            request.setValue(value, forHTTPHeaderField: name)
        }
        let (data, response) = try await uploadFile(fileURL, with: request, onProgress: onProgress)
        try validate(response: response, data: data)
        onProgress(1)
    }

    
    private func uploadFile(
        _ fileURL: URL,
        with request: URLRequest,
        onProgress: @escaping (Double) -> Void
    ) async throws -> (Data, URLResponse) {
        var task: URLSessionUploadTask?
        let polling = Task {
            while !Task.isCancelled {
                if let task {
                    onProgress(task.progress.fractionCompleted)
                    if task.state == .completed { return }
                }
                try? await Task.sleep(for: .milliseconds(200))
            }
        }
        defer { polling.cancel() }
        return try await withCheckedThrowingContinuation { continuation in
            task = session.uploadTask(with: request, fromFile: fileURL) { data, response, error in
                if let error { continuation.resume(throwing: error); return }
                guard let response else {
                    continuation.resume(throwing: CloudClientError.invalidResponse)
                    return
                }
                continuation.resume(returning: (data ?? Data(), response))
            }
            task?.resume()
        }
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
