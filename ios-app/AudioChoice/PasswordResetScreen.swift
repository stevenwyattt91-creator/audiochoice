import SwiftUI

/// Recovers an account whose password no longer works.
///
/// Until this existed a listener who could not sign in had no route back to their library. The only
/// option was a second account on a different address, which abandons the books in the first — and one
/// tester reached exactly that point after signing in on a second device.
///
/// The code is pasted rather than followed as a link. The account being recovered is only reachable in
/// the app, so sending someone to a browser adds a page to land on and a hand-off to come back from,
/// and both can fail. The email carries a link as well, for anyone reading it on a computer.
struct PasswordResetScreen: View {
    /// Carried in from the sign-in form, so a listener who typed their address once is not asked again.
    let initialEmail: String
    /// Called once the password is changed, with the address it belongs to.
    let onComplete: (String) -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var email = ""
    @State private var code = ""
    @State private var newPassword = ""
    /// Advanced only after the request is accepted, so nobody is asked for a code before one is sent.
    @State private var codeRequested = false
    @State private var working = false
    @State private var errorMessage: String?
    @State private var notice: String?

    private var canRequest: Bool { !working && !email.trimmed.isEmpty }
    private var canConfirm: Bool {
        !working && !code.trimmed.isEmpty && newPassword.count >= minimumPasswordLength
    }

    var body: some View {
        Form {
            if !codeRequested {
                Section("Reset Password") {
                    Text("Enter the email address on your account. We'll send you a code.")
                        .font(.footnote)
                        .foregroundStyle(ACTheme.secondaryText)
                    TextField("Email", text: $email)
                        .keyboardType(.emailAddress)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .textContentType(.emailAddress)
                    Button("Send Code") { Task { await request() } }
                        .disabled(!canRequest)
                }
            } else {
                Section("Enter Your Code") {
                    Text("Paste the code from the email, then choose a new password.")
                        .font(.footnote)
                        .foregroundStyle(ACTheme.secondaryText)
                    // Not a SecureField: the code is pasted from an email and hiding it would stop a
                    // listener seeing whether the paste worked, which is the step most likely to go
                    // wrong. It is single-use and expires within the hour.
                    TextField("Reset code", text: $code)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .font(.footnote.monospaced())
                    SecureField("New password", text: $newPassword)
                        .textContentType(.newPassword)
                    Text("Use at least \(minimumPasswordLength) characters.")
                        .font(.caption)
                        .foregroundStyle(ACTheme.secondaryText)
                    Button("Set New Password") { Task { await confirm() } }
                        .disabled(!canConfirm)
                    Button("Send another code") {
                        codeRequested = false
                        code = ""
                        notice = nil
                        errorMessage = nil
                    }
                    .font(.footnote)
                }
            }

            if let notice {
                Section { Text(notice).font(.footnote).foregroundStyle(ACTheme.secondaryText) }
            }
            if let errorMessage {
                Section {
                    Label(errorMessage, systemImage: "exclamationmark.triangle.fill")
                        .font(.footnote)
                        .foregroundStyle(.orange)
                }
            }
            if working {
                Section { ProgressView() }
            }
        }
        .navigationTitle("Password")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .cancellationAction) {
                Button("Cancel") { dismiss() }
            }
        }
        .acScreen()
        .onAppear { if email.isEmpty { email = initialEmail } }
    }

    private func request() async {
        working = true
        errorMessage = nil
        defer { working = false }
        do {
            try await AuthenticationClient().requestPasswordReset(email: email.trimmed)
            codeRequested = true
            // Worded without confirming the address has an account, matching what the server does.
            // Telling someone their email is unknown would let anyone discover who is registered.
            notice = "If that address has an account, a code is on its way. " +
                "It can take a minute or two to arrive, and it expires within the hour."
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func confirm() async {
        working = true
        errorMessage = nil
        defer { working = false }
        do {
            try await AuthenticationClient().confirmPasswordReset(
                // Trimmed, because copying from an email very often takes a trailing space or newline
                // with it, and a code that fails for an invisible reason is the worst kind.
                code: code.trimmed,
                newPassword: newPassword
            )
            onComplete(email.trimmed)
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    /// Matches the server's minimum, so the form refuses what the server would refuse.
    private var minimumPasswordLength: Int { 12 }
}

private extension String {
    var trimmed: String { trimmingCharacters(in: .whitespacesAndNewlines) }
}
