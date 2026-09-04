import SwiftUI

struct ParentalControlsScreen: View {
    /// Mirrors the Keychain so the form can react. The PIN itself is never held here,
    /// only whether one exists.
    @State private var pinIsSet = ParentalPinStore.isSet
    @State private var pin = ""
    @State private var confirmation = ""
    @State private var currentPin = ""
    @State private var message: String?

    var body: some View {
        Form {
            Section("Filter lock") {
                Text("A 4–6 digit PIN prevents others from changing audiobook filters on this device.")
                    .foregroundStyle(ACTheme.secondaryText)
                // An existing PIN always has to be entered to replace or remove it. The
                // previous version offered a "Set PIN" mode that skipped this check, so
                // anyone reaching this screen could overwrite the lock instead of opening
                // it, and then unlock the filters with a PIN of their own.
                if pinIsSet {
                    SecureField("Current PIN", text: $currentPin).keyboardType(.numberPad)
                }
                SecureField(pinIsSet ? "New PIN" : "PIN", text: $pin).keyboardType(.numberPad)
                SecureField("Confirm PIN", text: $confirmation).keyboardType(.numberPad)
                Button(pinIsSet ? "Change PIN" : "Set PIN") { save() }
                    .disabled(!ParentalPinStore.isValidPin(pin) || pin != confirmation)
                if pinIsSet {
                    Button("Remove PIN", role: .destructive) {
                        guard ParentalPinStore.verify(currentPin) else {
                            message = "Enter your current PIN before removing it."
                            return
                        }
                        ParentalPinStore.clear()
                        pinIsSet = false
                        currentPin = ""
                        message = "PIN removed."
                    }
                }
            }
            Section("What this does") {
                Text(pinIsSet
                     ? "Filters are protected on this device. You will be asked for this PIN before changes are made."
                     : "Filters are currently unlocked.")
            }
            if let message { Section { Text(message).foregroundStyle(ACTheme.accent) } }
        }
        .navigationTitle("Parental Controls")
        .acScreen()
        .onAppear { pinIsSet = ParentalPinStore.isSet }
    }

    private func save() {
        guard !pinIsSet || ParentalPinStore.verify(currentPin) else {
            message = "Your current PIN did not match."
            return
        }
        ParentalPinStore.set(pin)
        pinIsSet = ParentalPinStore.isSet
        pin = ""
        confirmation = ""
        currentPin = ""
        message = "PIN saved."
    }
}

struct FAQScreen: View {
    /// Starts from the bundled copy so the screen has answers on the first frame and offline, then
    /// prefers the served one when it arrives.
    @State private var faq = FaqLoader.bundled

    var body: some View {
        List {
            ForEach(faq.sections) { section in
                Section(section.title) {
                    ForEach(section.items) { item in
                        VStack(alignment: .leading, spacing: 7) {
                            Text(item.question).font(.headline)
                            Text(item.answer).foregroundStyle(ACTheme.secondaryText)
                        }
                        .padding(.vertical, 6)
                    }
                }
            }
        }
        .navigationTitle("FAQs")
        .acScreen()
        .task {
            // Compared by version rather than assumed newer: an app that has not been updated should
            // still show the better answers, and a server that has somehow fallen behind should not
            // replace them with worse ones. An empty reply is ignored for the same reason.
            if let served = await FaqLoader.fetch(),
               !served.sections.isEmpty,
               served.version >= faq.version {
                faq = served
            }
        }
    }
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
            Section { Text(sent ? "Thanks — your message was sent to the AudioChoice team." : "Include the audiobook name and what you expected to happen.").foregroundStyle(ACTheme.secondaryText) }
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


