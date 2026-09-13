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
    /// Optional. Never blocks account creation -- see the debounced check in `checkReferralCode`.
    @State private var referralCode = ""
    @State private var referralCodeValid: Bool?
    @State private var referralCheckTask: Task<Void, Never>?
    @State private var confirmingDelete = false
    @State private var deletingAccount = false

    /// Which field the keyboard is in, so Return advances instead of doing nothing.
    private enum Field: Hashable { case email, password, confirmPassword, referral }
    @FocusState private var focusedField: Field?
    @State private var revealPassword = false

    var body: some View {
        Group {
            if session.user == nil {
                signInLayout
            } else {
                signedInForm
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
        .confirmationDialog(
            "Delete your AudioChoice account?",
            isPresented: $confirmingDelete,
            titleVisibility: .visible
        ) {
            Button("Delete Account", role: .destructive) { Task { await deleteAccount() } }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This permanently deletes your library, filter choices, and account. This cannot be undone. If you have an active subscription, cancel it separately in your Apple ID subscription settings.")
        }
        .navigationTitle(isLaunchScreen ? "" : "Account")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar(isLaunchScreen ? .hidden : .visible, for: .navigationBar)
        .acScreen()
    }

    // MARK: - Signed out

    /// The first screen anyone sees, so it is built rather than borrowed.
    ///
    /// Deliberately not a `Form`. A form gave this screen iOS's grouped-list chrome -- system grey
    /// headers, system row fills -- on top of a near-black theme, with bare text fields that showed
    /// no edge, no focus and no sign of being controls. Every field now sits in `ACField`, the
    /// keyboard walks the form in order, and the call to action matches Subscribe on the paywall.
    private var signInLayout: some View {
        ScrollView {
            VStack(spacing: 20) {
                if isLaunchScreen { brandMark.padding(.top, 8) }

                Picker("", selection: $creatingAccount) {
                    Text("Sign In").tag(false)
                    Text("Create Account").tag(true)
                }
                .pickerStyle(.segmented)
                .onChange(of: creatingAccount) { _, _ in
                    confirmPassword = ""
                    errorMessage = nil
                    focusedField = nil
                }

                VStack(spacing: 12) {
                    ACField(icon: "envelope", isFocused: focusedField == .email) {
                        TextField("Email", text: $email)
                            .keyboardType(.emailAddress)
                            .textInputAutocapitalization(.never)
                            .textContentType(.emailAddress)
                            .autocorrectionDisabled()
                            .focused($focusedField, equals: .email)
                            .submitLabel(.next)
                            .onSubmit { focusedField = .password }
                    }

                    ACField(
                        icon: "lock",
                        isFocused: focusedField == .password,
                        accessory: AnyView(revealToggle)
                    ) {
                        passwordEntry(
                            "Password",
                            text: $password,
                            field: .password,
                            contentType: creatingAccount ? .newPassword : .password,
                            submitLabel: creatingAccount ? .next : .go
                        ) {
                            if creatingAccount {
                                focusedField = .confirmPassword
                            } else if canSubmit {
                                Task { await submitEmail() }
                            }
                        }
                    }

                    if creatingAccount {
                        ACField(icon: "lock.rotation", isFocused: focusedField == .confirmPassword) {
                            passwordEntry(
                                "Confirm password",
                                text: $confirmPassword,
                                field: .confirmPassword,
                                contentType: .newPassword,
                                submitLabel: .next
                            ) { focusedField = .referral }
                        }

                        // Both notes sit under the fields they describe rather than in a section of
                        // their own, which is what a form forced.
                        fieldNote(
                            !confirmPassword.isEmpty && confirmPassword != password
                                ? "Those passwords do not match."
                                : "Use at least 12 characters.",
                            tone: !confirmPassword.isEmpty && confirmPassword != password
                                ? .orange
                                : ACTheme.secondaryText
                        )

                        ACField(icon: "tag", isFocused: focusedField == .referral) {
                            TextField("Referral code (optional)", text: $referralCode)
                                .textInputAutocapitalization(.characters)
                                .autocorrectionDisabled()
                                .focused($focusedField, equals: .referral)
                                .submitLabel(.go)
                                .onSubmit { if canSubmit { Task { await submitEmail() } } }
                                .onChange(of: referralCode) { _, newValue in checkReferralCode(newValue) }
                        }

                        if !referralCode.isEmpty, let referralCodeValid {
                            fieldNote(
                                referralCodeValid
                                    ? "Referral code accepted."
                                    : "That code was not recognized, but you can still create your account.",
                                tone: referralCodeValid ? ACTheme.accent : ACTheme.secondaryText
                            )
                        }
                    }
                }

                if let errorMessage {
                    // Beside the button that produced it, not in a section at the bottom of the
                    // screen where it could be scrolled past unseen.
                    HStack(alignment: .top, spacing: 10) {
                        Image(systemName: "exclamationmark.triangle.fill")
                            .foregroundStyle(.orange)
                        Text(errorMessage)
                            .font(.footnote)
                            .foregroundStyle(.orange)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .padding(14)
                    .background(Color.orange.opacity(0.10))
                    .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
                }

                Button {
                    focusedField = nil
                    Task { await submitEmail() }
                } label: {
                    // The spinner replaces the label rather than sitting beside it, so the button
                    // does not change width partway through a sign-in.
                    if working {
                        ProgressView().tint(.black)
                    } else {
                        Text(creatingAccount ? "Create Account" : "Sign In")
                    }
                }
                .buttonStyle(ACPrimaryButtonStyle())
                .disabled(!canSubmit)

                if !creatingAccount {
                    Button("Forgot password?") { resettingPassword = true }
                        .font(.footnote)
                        .foregroundStyle(ACTheme.secondaryText)
                }

                ACDivider(label: "or continue with")
                    .padding(.vertical, 2)

                VStack(spacing: 10) {
                    SignInWithAppleButton(.signIn) { request in
                        request.requestedScopes = [.fullName, .email]
                    } onCompletion: { result in
                        Task { await handleApple(result) }
                    }
                    .signInWithAppleButtonStyle(.white)
                    .frame(height: 54)
                    .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
                    .disabled(working)

                    GoogleSignInButton(action: { Task { await signInWithGoogle() } })
                        .frame(height: 54)
                        .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
                        .disabled(working)
                }

                Spacer(minLength: 12)
            }
            .padding(.horizontal, 24)
            .padding(.vertical, 20)
        }
        // Tapping the background puts the keyboard away, which a form gave for free and a
        // ScrollView does not.
        .scrollDismissesKeyboard(.interactively)
        .contentShape(Rectangle())
        .onTapGesture { focusedField = nil }
    }

    private var brandMark: some View {
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
        .padding(.bottom, 6)
    }

    private var revealToggle: some View {
        Button {
            revealPassword.toggle()
        } label: {
            Image(systemName: revealPassword ? "eye.slash" : "eye")
                .font(.system(size: 15))
                .foregroundStyle(ACTheme.secondaryText)
        }
        .buttonStyle(.plain)
        .accessibilityLabel(revealPassword ? "Hide password" : "Show password")
    }

    /// A secure field that can be revealed, with every modifier applied to both forms.
    ///
    /// Written out twice rather than switching a `Group`, because the two are different view types
    /// and attaching focus to the conditional wrapper instead of the field itself is how the
    /// keyboard ends up refusing to advance.
    @ViewBuilder
    private func passwordEntry(
        _ placeholder: String,
        text: Binding<String>,
        field: Field,
        contentType: UITextContentType,
        submitLabel: SubmitLabel,
        onSubmit action: @escaping () -> Void
    ) -> some View {
        if revealPassword {
            TextField(placeholder, text: text)
                .textContentType(contentType)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .focused($focusedField, equals: field)
                .submitLabel(submitLabel)
                .onSubmit(action)
        } else {
            SecureField(placeholder, text: text)
                .textContentType(contentType)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .focused($focusedField, equals: field)
                .submitLabel(submitLabel)
                .onSubmit(action)
        }
    }

    private func fieldNote(_ text: String, tone: Color) -> some View {
        Text(text)
            .font(.caption)
            .foregroundStyle(tone)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, 4)
    }

    // MARK: - Signed in

    /// Kept as a form on purpose: reached from Profile, it is a settings screen and reads correctly
    /// as one. Only the front door needed rebuilding.
    private var signedInForm: some View {
        Form {
            if let user = session.user {
                Section("Signed In") {
                    LabeledContent("Name", value: user.displayName)
                    LabeledContent("Email", value: user.email)
                    LabeledContent("Method", value: user.provider.capitalized)
                    Button("Sign Out", role: .destructive) { session.signOut() }
                    Button("Delete Account", role: .destructive) { confirmingDelete = true }
                        .disabled(deletingAccount)
                }
            }
            if let errorMessage {
                Section { Text(errorMessage).foregroundStyle(.orange) }
            }
        }
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
                ? try await client.register(email: email, password: password, referralCode: referralCode)
                : try await client.login(email: email, password: password)
            session.accept(response)
        } catch { errorMessage = error.localizedDescription }
    }

    private func deleteAccount() async {
        deletingAccount = true
        defer { deletingAccount = false }
        do {
            try await session.deleteAccount()
        } catch { errorMessage = error.localizedDescription }
    }

    /// Debounced rather than checked on every keystroke, and never blocks the Create Account button
    /// either way: this is purely informational, since the server itself never rejects a signup over
    /// an unknown code.
    private func checkReferralCode(_ code: String) {
        referralCheckTask?.cancel()
        guard !code.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            referralCodeValid = nil
            return
        }
        referralCheckTask = Task {
            try? await Task.sleep(nanoseconds: 500_000_000)
            guard !Task.isCancelled else { return }
            let result = try? await AuthenticationClient().checkReferralCode(code)
            guard !Task.isCancelled else { return }
            referralCodeValid = result?.valid
        }
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
