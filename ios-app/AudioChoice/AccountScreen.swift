import AuthenticationServices
import GoogleSignIn
import GoogleSignInSwift
import SwiftUI
import UIKit

struct AccountScreen: View {
    @ObservedObject private var session = AuthSession.shared
    @State private var email = ""
    @State private var password = ""
    @State private var displayName = ""
    @State private var creatingAccount = false
    @State private var working = false
    @State private var errorMessage: String?

    var body: some View {
        Form {
            if let user = session.user {
                Section("Signed In") {
                    LabeledContent("Name", value: user.displayName)
                    LabeledContent("Email", value: user.email)
                    LabeledContent("Method", value: user.provider.capitalized)
                    Button("Sign Out", role: .destructive) { session.signOut() }
                }
            } else {
                Section(creatingAccount ? "Create Account" : "Sign In") {
                    if creatingAccount {
                        TextField("Username", text: $displayName)
                            .textContentType(.username)
                    }
                    TextField("Email", text: $email)
                        .keyboardType(.emailAddress)
                        .textInputAutocapitalization(.never)
                        .textContentType(.emailAddress)
                    SecureField("Password", text: $password)
                        .textContentType(creatingAccount ? .newPassword : .password)
                    if creatingAccount {
                        Text("Use at least 12 characters.")
                            .font(.caption)
                            .foregroundStyle(ACTheme.secondaryText)
                    }
                    Button(creatingAccount ? "Create Account" : "Sign In") {
                        Task { await submitEmail() }
                    }
                    .disabled(working || email.isEmpty || password.isEmpty)
                }

                Section("Or continue with") {
                    SignInWithAppleButton(.signIn) { request in
                        request.requestedScopes = [.fullName, .email]
                    } onCompletion: { result in
                        Task { await handleApple(result) }
                    }
                    .signInWithAppleButtonStyle(.white)
                    .frame(height: 50)
                    .disabled(working)

                    GoogleSignInButton(action: { Task { await signInWithGoogle() } })
                        .frame(height: 50)
                    .disabled(working)
                }

                Section {
                    Button(creatingAccount ? "Already have an account? Sign In" : "New to AudioChoice? Create Account") {
                        creatingAccount.toggle()
                        errorMessage = nil
                    }
                }
            }

            if let errorMessage {
                Section { Text(errorMessage).foregroundStyle(.orange) }
            }
        }
        .navigationTitle("Account")
        .navigationBarTitleDisplayMode(.inline)
        .acScreen()
    }

    private func submitEmail() async {
        working = true
        defer { working = false }
        do {
            let client = try AuthenticationClient()
            let response = creatingAccount
                ? try await client.register(email: email, password: password, displayName: displayName)
                : try await client.login(email: email, password: password)
            session.accept(response)
        } catch { errorMessage = error.localizedDescription }
    }

    private func handleApple(_ result: Result<ASAuthorization, Error>) async {
        working = true
        defer { working = false }
        do {
            guard case let .success(authorization) = result,
                  let credential = authorization.credential as? ASAuthorizationAppleIDCredential else {
                if case let .failure(error) = result { throw error }
                throw AuthenticationError.invalidAppleCredential
            }
            session.accept(try await AuthenticationClient().signInWithApple(credential: credential))
        } catch { errorMessage = error.localizedDescription }
    }

    private func signInWithGoogle() async {
        working = true
        defer { working = false }
        do {
            let configuration = try GoogleSignInConfiguration.load()
            GIDSignIn.sharedInstance.configuration = configuration
            guard let presenter = UIApplication.shared.activePresentationController else {
                throw AuthenticationError.noPresentationContext
            }
            let result = try await GIDSignIn.sharedInstance.signIn(withPresenting: presenter)
            guard let identityToken = result.user.idToken?.tokenString else {
                throw AuthenticationError.invalidGoogleCredential
            }
            session.accept(
                try await AuthenticationClient().signInWithGoogle(
                    authorizationCode: result.serverAuthCode ?? "",
                    identityToken: identityToken,
                    displayName: result.user.profile?.name
                )
            )
        } catch { errorMessage = error.localizedDescription }
    }
}

private struct GoogleSignInConfiguration {
    static func load() throws -> GIDConfiguration {
        guard let clientID = Bundle.main.object(forInfoDictionaryKey: "GoogleIOSClientID") as? String,
              !clientID.isEmpty,
              !clientID.hasPrefix("REPLACE_") else {
            throw AuthenticationError.googleNotConfigured
        }
        let serverClientID = Bundle.main.object(forInfoDictionaryKey: "GoogleServerClientID") as? String
        return GIDConfiguration(clientID: clientID, serverClientID: serverClientID)
    }
}

private extension UIApplication {
    var activePresentationController: UIViewController? {
        connectedScenes
            .compactMap { $0 as? UIWindowScene }
            .flatMap(\.windows)
            .first(where: { $0.isKeyWindow })?
            .rootViewController
    }
}
