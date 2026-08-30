import AuthenticationServices
import GoogleSignIn
import GoogleSignInSwift
import SwiftUI
import UIKit

struct AccountScreen: View {
    var isLaunchScreen = false
    @ObservedObject private var session = AuthSession.shared
    @State private var email = ""
    @State private var password = ""
    /// Typed a second time when creating an account, and compared before anything is sent.
    ///
    /// A mistyped password at sign-up is the worst kind: it is accepted, and the listener is then
    /// locked out of an account they only just made, with the password they meant to use.
    @State private var confirmPassword = ""
    @State private var creatingAccount = false
    @State private var resettingPassword = false
    @State private var working = false
    @State private var errorMessage: String?

    var body: some View {
        Form {
            if isLaunchScreen && session.user == nil {
                Section {
                    VStack(spacing: 8) {
                        Image(systemName: "headphones")
                            .font(.system(size: 54, weight: .light))
                            .foregroundStyle(ACTheme.accent)
                        HStack(spacing: 0) {
                            Text("Audio")
                            Text("Choice").foregroundStyle(ACTheme.accent)
                        }
                        .font(.largeTitle.bold())
                        Text("Listen Your Way")
                            .foregroundStyle(ACTheme.secondaryText)
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 20)
                    .listRowBackground(Color.clear)
                }
            }

            if let user = session.user {
                Section("Signed In") {
                    LabeledContent("Name", value: user.displayName)
                    LabeledContent("Email", value: user.email)
                    LabeledContent("Method", value: user.provider.capitalized)
                    Button("Sign Out", role: .destructive) { session.signOut() }
                }
            } else {
                Section(creatingAccount ? "Create Account" : "Sign In") {
                    TextField("Email", text: $email)
                        .keyboardType(.emailAddress)
                        .textInputAutocapitalization(.never)
                        .textContentType(.emailAddress)
                    SecureField("Password", text: $password)
                        .textContentType(creatingAccount ? .newPassword : .password)
                    if creatingAccount {
                        SecureField("Confirm password", text: $confirmPassword)
                            .textContentType(.newPassword)
                        Text("Use at least 12 characters.")
                            .font(.caption)
                            .foregroundStyle(ACTheme.secondaryText)
                        // Said as soon as they diverge, rather than on submit. Finding out after
                        // pressing Create means retyping both fields.
                        if !confirmPassword.isEmpty && confirmPassword != password {
                            Text("Those passwords do not match.")
                                .font(.caption)
                                .foregroundStyle(.orange)
                        }
                    }
                    Button(creatingAccount ? "Create Account" : "Sign In") {
                        Task { await submitEmail() }
                    }
                    .disabled(!canSubmit)
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
                        confirmPassword = ""
                        errorMessage = nil
                    }
                    // Offered on the sign-in path only. Someone creating an account has no password
                    // to recover, and until this existed a listener who could not sign in had no
                    // route back to their library at all -- their only option was a second account
                    // on a different address, abandoning the books in the first.
                    if !creatingAccount {
                        Button("Forgot password?") { resettingPassword = true }
                    }
                }
            }

            if let errorMessage {
                Section { Text(errorMessage).foregroundStyle(.orange) }
            }
        }
        .sheet(isPresented: $resettingPassword) {
            NavigationStack {
                PasswordResetScreen(initialEmail: email) { restoredEmail in
                    // Carried back so they are not asked to type it a second time, and the password
                    // field is left empty rather than prefilled with the one that did not work.
                    email = restoredEmail
                    password = ""
                    resettingPassword = false
                    errorMessage = nil
                }
            }
        }
        .navigationTitle(isLaunchScreen ? "" : "Account")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar(isLaunchScreen ? .hidden : .visible, for: .navigationBar)
        .acScreen()
    }

    /// Whether the form is complete enough to send.
    ///
    /// The confirmation and the minimum length are checked here as well as by the server, so the
    /// button refuses what the server would refuse rather than spending a round trip to be told.
    private var canSubmit: Bool {
        if working || email.isEmpty || password.isEmpty { return false }
        guard creatingAccount else { return true }
        return password == confirmPassword && password.count >= 12
    }

    private func submitEmail() async {
        working = true
        defer { working = false }
        do {
            let client = try AuthenticationClient()
            let response = creatingAccount
                ? try await client.register(email: email, password: password)
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
        guard let reversedClientID = Bundle.main.object(forInfoDictionaryKey: "GoogleReversedClientID") as? String,
              !reversedClientID.isEmpty,
              !reversedClientID.hasPrefix("REPLACE_") else {
            throw AuthenticationError.googleNotConfigured
        }
        // The API validates Google's ID token directly and does not exchange a
        // server authorization code. Using only the iOS client keeps the token's
        // audience aligned with this app (the API accepts this iOS client ID).
        return GIDConfiguration(clientID: clientID)
    }
}

private extension UIApplication {
    var activePresentationController: UIViewController? {
        let root = connectedScenes
            .compactMap { $0 as? UIWindowScene }
            .flatMap(\.windows)
            .first(where: { $0.isKeyWindow })?
            .rootViewController
        var visible = root
        while let presented = visible?.presentedViewController {
            visible = presented
        }
        return visible
    }
}
