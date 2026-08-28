import SwiftUI

/// Saved filter profiles: say what you never want to hear once, not per book.
///
/// A profile only decides where a *new* book starts. It never reaches back into a book that
/// already has choices of its own, because someone who tuned a book by hand has said
/// something more specific than the profile does.
struct FilterProfilesScreen: View {
    @StateObject private var store = FilterProfileStore.shared
    @State private var pinIsSet = ParentalPinStore.isSet
    @State private var unlocked = false
    @State private var showingPinPrompt = false
    @State private var enteredPin = ""
    @State private var pinError: String?

    private var isLocked: Bool { pinIsSet && !unlocked }

    var body: some View {
        ScrollView {
            VStack(spacing: 16) {
                ACCard {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("A profile decides where a new book starts. Books you have already adjusted keep their own settings.")
                            .font(.footnote)
                            .foregroundStyle(ACTheme.secondaryText)
                            .fixedSize(horizontal: false, vertical: true)
                        Text("Profiles carry whole categories and groups. A single line or word you allowed through in one book stays with that book.")
                            .font(.caption)
                            .foregroundStyle(ACTheme.secondaryText)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }

                if isLocked {
                    ACCard {
                        HStack {
                            Label("Profiles are locked", systemImage: "lock.fill")
                                .foregroundStyle(ACTheme.accent)
                            Spacer()
                            Button("Enter PIN") {
                                pinError = nil
                                showingPinPrompt = true
                            }
                            .foregroundStyle(ACTheme.accent)
                        }
                    }
                }

                if let pinError {
                    ACCard { Text(pinError).font(.footnote).foregroundStyle(.red) }
                }

                if store.profiles.isEmpty {
                    ACCard {
                        Text(store.isLoading
                             ? "Loading your profiles…"
                             : "No profiles yet. Open a book's Playback Filters, set it up how you want, then choose Save as Profile.")
                            .font(.footnote)
                            .foregroundStyle(ACTheme.secondaryText)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                } else {
                    ACCard {
                        VStack(alignment: .leading, spacing: 0) {
                            ForEach(store.profiles) { profile in
                                profileRow(profile)
                                if profile.id != store.profiles.last?.id { Divider() }
                            }
                        }
                    }
                }

                if let message = store.errorMessage {
                    ACCard {
                        Text(message).font(.caption).foregroundStyle(.orange)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
            }
            .padding()
        }
        .background(ACTheme.background)
        .navigationTitle("Filter Profiles")
        .navigationBarTitleDisplayMode(.inline)
        .task {
            pinIsSet = ParentalPinStore.isSet
            await store.refresh()
        }
        .alert("Enter parental PIN", isPresented: $showingPinPrompt) {
            SecureField("PIN", text: $enteredPin).keyboardType(.numberPad)
            Button("Unlock") {
                if ParentalPinStore.verify(enteredPin) {
                    unlocked = true
                    pinError = nil
                } else {
                    pinError = "That PIN did not match. Profiles are still locked."
                }
                enteredPin = ""
            }
            Button("Cancel", role: .cancel) { enteredPin = "" }
        } message: {
            Text("Enter the PIN to change saved profiles.")
        }
    }

    private func profileRow(_ profile: FilterProfile) -> some View {
        HStack(spacing: 12) {
            Image(systemName: profile.isActive ? "checkmark.circle.fill" : "circle")
                .foregroundStyle(profile.isActive ? ACTheme.accent : ACTheme.secondaryText)
            VStack(alignment: .leading, spacing: 2) {
                Text(profile.name)
                Text(summary(profile))
                    .font(.caption)
                    .foregroundStyle(ACTheme.secondaryText)
            }
            Spacer()
            if !profile.isActive {
                Button("Use") { Task { await store.activate(profile) } }
                    .font(.caption.bold())
                    .foregroundStyle(ACTheme.accent)
                    .disabled(isLocked)
            }
            Button(role: .destructive) {
                Task { await store.delete(profile) }
            } label: {
                Image(systemName: "trash").foregroundStyle(.red)
            }
            .disabled(isLocked)
            .accessibilityLabel("Delete \(profile.name)")
        }
        .padding(.vertical, 10)
    }

    private func summary(_ profile: FilterProfile) -> String {
        let off = profile.rules.filter { !$0.enabled }.count
        if profile.isActive {
            return off == 0 ? "Active · filters everything" : "Active · \(off) turned off"
        }
        return off == 0 ? "Filters everything" : "\(off) turned off"
    }
}
