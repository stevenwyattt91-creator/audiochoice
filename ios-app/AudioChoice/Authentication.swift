import AuthenticationServices
import Foundation

struct AuthUser: Codable {
    let id: UUID
    let email: String
    let displayName: String
    let provider: String
}

struct AuthResponse: Codable {
    let accessToken: String
    let expiresAt: Date
    let user: AuthUser
}

enum AuthenticationError: LocalizedError {
    case missingServer
    case rejected(String)
    case invalidAppleCredential
    case invalidGoogleCredential
    case googleNotConfigured
    case noPresentationContext

    var errorDescription: String? {
        switch self {
        case .missingServer: "Add the AudioChoice server address under Cloud Connection first."
        case let .rejected(message): message
        case .invalidAppleCredential: "Apple did not return a usable sign-in credential."
        case .invalidGoogleCredential: "Google did not return a usable sign-in credential."
        case .googleNotConfigured: "Google sign-in has not been connected to this iOS app yet."
        case .noPresentationContext: "Google sign-in could not open from this screen. Please try again."
        }
    }
}

struct AuthenticationClient {
    private let baseURL: URL

    init() throws {
        let savedAddress = UserDefaults.standard.string(forKey: "cloudBaseURL")?
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let bundledAddress = (Bundle.main.object(forInfoDictionaryKey: "AudioChoiceAPIBaseURL") as? String)?
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        // Shipped builds use their bundled API endpoint. The saved value remains
        // a fallback for local development builds that do not bundle one.
        let address = bundledAddress.isEmpty ? savedAddress : bundledAddress
        guard let url = URL(string: address), !address.isEmpty else { throw AuthenticationError.missingServer }
        baseURL = url
    }

    func register(email: String, password: String, displayName: String) async throws -> AuthResponse {
        try await post(["email": email, "password": password, "displayName": displayName], path: "v1/auth/register")
    }

    func login(email: String, password: String) async throws -> AuthResponse {
        try await post(["email": email, "password": password], path: "v1/auth/login")
    }

    func signInWithApple(credential: ASAuthorizationAppleIDCredential) async throws -> AuthResponse {
        guard let code = credential.authorizationCode.flatMap({ String(data: $0, encoding: .utf8) }) else {
            throw AuthenticationError.invalidAppleCredential
        }
        let token = credential.identityToken.flatMap { String(data: $0, encoding: .utf8) }
        let name = [credential.fullName?.givenName, credential.fullName?.familyName]
            .compactMap { $0 }.joined(separator: " ")
        return try await post(
            ExternalRequest(provider: "apple", authorizationCode: code, identityToken: token, displayName: name),
            path: "v1/auth/external"
        )
    }

    func signInWithGoogle(
        authorizationCode: String,
        identityToken: String,
        displayName: String?
    ) async throws -> AuthResponse {
        guard !identityToken.isEmpty else { throw AuthenticationError.invalidGoogleCredential }
        return try await post(
            ExternalRequest(
                provider: "google",
                authorizationCode: authorizationCode,
                identityToken: identityToken,
                displayName: displayName
            ),
            path: "v1/auth/external"
        )
    }

    private func post<Input: Encodable>(_ value: Input, path: String) async throws -> AuthResponse {
        let url = path.split(separator: "/").reduce(baseURL) { $0.appendingPathComponent(String($1)) }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(value)
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            let message = (try? JSONDecoder().decode(ServerMessage.self, from: data).error) ?? "Sign in was not accepted."
            throw AuthenticationError.rejected(message)
        }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try decoder.decode(AuthResponse.self, from: data)
    }

    private struct ExternalRequest: Encodable {
        let provider: String
        let authorizationCode: String
        let identityToken: String?
        let displayName: String?
    }
    private struct ServerMessage: Decodable { let error: String }
}

@MainActor
final class AuthSession: ObservableObject {
    static let shared = AuthSession()
    @Published private(set) var user: AuthUser?
    private let userKey = "authenticatedUser"

    private init() {
        let token = CloudCredentialStore.loadToken()
            .trimmingCharacters(in: .whitespacesAndNewlines)
        if !token.isEmpty,
           let data = UserDefaults.standard.data(forKey: userKey) {
            user = try? JSONDecoder().decode(AuthUser.self, from: data)
        } else {
            UserDefaults.standard.removeObject(forKey: userKey)
        }
    }

    func accept(_ response: AuthResponse) {
        CloudCredentialStore.saveToken(response.accessToken)
        user = response.user
        if let data = try? JSONEncoder().encode(response.user) { UserDefaults.standard.set(data, forKey: userKey) }
    }

    func signOut() {
        CloudCredentialStore.saveToken("")
        UserDefaults.standard.removeObject(forKey: userKey)
        user = nil
    }
}
