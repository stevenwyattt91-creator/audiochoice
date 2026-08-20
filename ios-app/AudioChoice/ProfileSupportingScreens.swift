import SwiftUI

struct ParentalControlsScreen: View {
    @AppStorage("parentalPin") private var savedPin = ""
    @State private var pin = ""
    @State private var confirmation = ""
    @State private var currentPin = ""
    @State private var mode: Mode = .setup
    @State private var message: String?

    private enum Mode: String, CaseIterable, Identifiable { case setup = "Set PIN", change = "Change PIN"; var id: String { rawValue } }
    var body: some View {
        Form {
            Section("Filter lock") {
                Text("A 4–6 digit PIN prevents others from changing audiobook filters on this device.")
                    .foregroundStyle(ACTheme.secondaryText)
                if !savedPin.isEmpty { Picker("", selection: $mode) { ForEach(Mode.allCases) { Text($0.rawValue).tag($0) } }.pickerStyle(.segmented) }
                if mode == .change && !savedPin.isEmpty { SecureField("Current PIN", text: $currentPin).keyboardType(.numberPad) }
                SecureField(mode == .change ? "New PIN" : "PIN", text: $pin).keyboardType(.numberPad)
                SecureField("Confirm PIN", text: $confirmation).keyboardType(.numberPad)
                Button(mode == .change ? "Change PIN" : (savedPin.isEmpty ? "Set PIN" : "Replace PIN")) { save() }
                    .disabled(!validPin(pin) || pin != confirmation)
                if !savedPin.isEmpty { Button("Remove PIN", role: .destructive) { if currentPin == savedPin { savedPin = ""; currentPin = ""; message = "PIN removed." } else { message = "Enter your current PIN before removing it." } } }
            }
            Section("What this does") { Text(savedPin.isEmpty ? "Filters are currently unlocked." : "Filters are protected on this device. You will be asked for this PIN before changes are made.") }
            if let message { Section { Text(message).foregroundStyle(ACTheme.accent) } }
        }.navigationTitle("Parental Controls").acScreen()
    }
    private func validPin(_ value: String) -> Bool { value.count >= 4 && value.count <= 6 && value.allSatisfy(\.isNumber) }
    private func save() {
        guard mode != .change || currentPin == savedPin else { message = "Your current PIN did not match."; return }
        savedPin = pin; pin = ""; confirmation = ""; currentPin = ""; message = "PIN saved."
    }
}

struct FAQScreen: View {
    var body: some View {
        List {
            Section("Importing") { faq("How do I add an audiobook?", "Use Import to choose your audiobook. Audio stays in the app’s private storage on your device.") }
            Section("Filters") { faq("How do filters work?", "AudioChoice uses a saved scan to skip or mute the categories you select. Your choices can be protected with a parental-controls PIN.") }
            Section("Your library") { faq("Will I lose my library when local audio is removed?", "No. Your book identity and last saved listening time follow your account. Re-import the audio to listen again.") }
            Section("Privacy") { faq("Is my audiobook public?", "No. Audio stays private. The app uses a fingerprint to find reusable scan results before uploading anything for a new scan.") }
        }.navigationTitle("FAQs").acScreen()
    }
    private func faq(_ question: String, _ answer: String) -> some View { VStack(alignment: .leading, spacing: 7) { Text(question).font(.headline); Text(answer).foregroundStyle(ACTheme.secondaryText) }.padding(.vertical, 6) }
}

struct SupportFormScreen: View {
    @State private var subject = ""
    @State private var message = ""
    @State private var sent = false
    @State private var sending = false
    @State private var errorMessage: String?
    var body: some View {
        Form {
            Section("Contact support") {
                TextField("Subject", text: $subject)
                TextEditor(text: $message).frame(minHeight: 160).overlay(alignment: .topLeading) { if message.isEmpty { Text("Tell us what happened…").foregroundStyle(.secondary).padding(.top, 8).allowsHitTesting(false) } }
                Button(sent ? "Message sent" : (sending ? "Sending…" : "Send message")) { Task { await send() } }
                    .disabled(sending || subject.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || message.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
            Section { Text(sent ? "Thanks — your message was sent to the AudioChoice team." : "Include the audiobook name and what you expected to happen. You can also use Submit Feedback for beta issues.").foregroundStyle(ACTheme.secondaryText) }
            if let errorMessage { Section { Text(errorMessage).foregroundStyle(.orange) } }
        }.navigationTitle("Support").acScreen()
    }
    private func send() async {
        sending = true; defer { sending = false }
        do {
            let client = try CloudScanClient.configured()
            _ = try await client.sendSupportMessage(subject: subject.trimmingCharacters(in: .whitespacesAndNewlines), message: message.trimmingCharacters(in: .whitespacesAndNewlines))
            sent = true; subject = ""; message = ""
        } catch { errorMessage = error.localizedDescription }
    }
}

struct BetaFeedbackScreen: View {
    enum Destination { case discord, feedback }
    let destination: Destination
    @Environment(\.openURL) private var openURL
    @State private var unavailable = false
    private var url: URL? {
        let key = destination == .discord ? "betaDiscordURL" : "betaFeedbackURL"
        return UserDefaults.standard.string(forKey: key).flatMap(URL.init(string:))
    }
    var body: some View {
        VStack(spacing: 20) {
            Image(systemName: destination == .discord ? "message" : "square.and.pencil").font(.system(size: 46)).foregroundStyle(ACTheme.accent)
            Text(destination == .discord ? "AudioChoice Beta Community" : "Submit Beta Feedback").font(.title2.bold())
            Text(destination == .discord ? "Join the community to discuss the beta." : "Tell us about playback, filter timing, or anything that needs improvement.").multilineTextAlignment(.center).foregroundStyle(ACTheme.secondaryText)
            Button(destination == .discord ? "Open Discord" : "Open feedback form") { if let url { openURL(url) } else { unavailable = true } }.buttonStyle(.borderedProminent).tint(ACTheme.accent).foregroundStyle(.black)
        }.padding().navigationTitle("Feedback").alert("Link not configured", isPresented: $unavailable) { Button("OK", role: .cancel) {} } message: { Text("The beta link will be added before release.") }
    }
}
