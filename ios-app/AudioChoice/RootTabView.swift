import SwiftUI

struct RootTabView: View {
    @Environment(\.scenePhase) private var scenePhase
    @ObservedObject private var scanRecovery = ScanRecoveryManager.shared
    @State private var selectedTab = 0
    @State private var importFlowID = UUID()
    var body: some View {
        TabView(selection: $selectedTab) {
            NavigationStack {
                LibraryScreen()
            }
            .tabItem { Label("Library", systemImage: "books.vertical.fill") }
            .tag(0)

            NavigationStack {
                NowPlayingScreen()
            }
            .tabItem { Label("Player", systemImage: "waveform") }
            .tag(1)

            NavigationStack {
                ImportScreen()
            }
            .id(importFlowID)
            .tabItem { Label("Import", systemImage: "square.and.arrow.down") }
            .tag(2)

            NavigationStack {
                ProfileScreen()
            }
            .tabItem { Label("Profile", systemImage: "person") }
            .tag(3)
        }
        .tint(ACTheme.accent)
        .task { await scanRecovery.recoverPendingScans() }
        .onReceive(NotificationCenter.default.publisher(for: .showAudioChoiceLibrary)) { _ in
            importFlowID = UUID()
            selectedTab = 0
        }
        .onChange(of: scenePhase) {
            if scenePhase == .active {
                Task { await scanRecovery.recoverPendingScans() }
            } else if scenePhase == .inactive || scenePhase == .background {
                Task { await AudioPlaybackManager.shared.syncCurrentProgressToAccount() }
            }
        }
    }
}

extension Notification.Name {
    static let showAudioChoiceLibrary = Notification.Name("showAudioChoiceLibrary")
}

struct ProfileScreen: View {
    @ObservedObject private var session = AuthSession.shared
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                Text("Audio") + Text("Choice").foregroundStyle(ACTheme.accent)
                Text("Profile").font(.largeTitle.bold())
                accountCard
                ACCard { VStack(spacing: 0) {
                    profileLink("Premium", "The most natural narration voice", "star", PremiumScreen())
                    Divider().overlay(ACTheme.secondaryText.opacity(0.25))
                    profileLink("Filter Profiles", "Set what you never want to hear once", "slider.horizontal.3", FilterProfilesScreen())
                    Divider().overlay(ACTheme.secondaryText.opacity(0.25))
                    profileLink("Parental Controls", "Protect audiobook filters with a PIN", "lock", ParentalControlsScreen())
                    Divider().overlay(ACTheme.secondaryText.opacity(0.25))
                    profileLink("FAQs", "Answers about importing, privacy, and filters", "questionmark.circle", FAQScreen())
                    Divider().overlay(ACTheme.secondaryText.opacity(0.25))
                    profileLink("Support", "Send a message to the AudioChoice team", "headphones", SupportFormScreen())
                } }
                NavigationLink { CloudConnectionScreen() } label: { Label("Cloud connection", systemImage: "network").font(.footnote).foregroundStyle(ACTheme.secondaryText) }
                .padding(.horizontal, 8)
            }.padding().padding(.bottom, 24)
        }.background(ACTheme.background)
    }

    private var accountCard: some View {
        NavigationLink { AccountScreen() } label: {
            ACCard { HStack(spacing: 18) {
                Image(systemName: "person.crop.circle").font(.system(size: 58)).foregroundStyle(ACTheme.accent)
                VStack(alignment: .leading, spacing: 5) {
                    Text(session.user?.displayName ?? "Sign in to AudioChoice").font(.title3.bold())
                    Text(session.user?.email ?? "Keep your library and listening position with your account").foregroundStyle(ACTheme.secondaryText).lineLimit(2)
                    Text(session.user.map { "Signed in with \($0.provider)" } ?? "Sign in").font(.subheadline).foregroundStyle(ACTheme.accent)
                }; Spacer()
            } }
        }.buttonStyle(.plain)
    }

    private func profileLink<Destination: View>(_ title: String, _ detail: String, _ icon: String, _ destination: Destination) -> some View {
        NavigationLink { destination } label: { HStack(spacing: 16) {
            Image(systemName: icon).font(.title2).foregroundStyle(ACTheme.accent).frame(width: 40)
            VStack(alignment: .leading, spacing: 4) { Text(title).font(.headline); Text(detail).font(.subheadline).foregroundStyle(ACTheme.secondaryText) }
            Spacer(); Image(systemName: "chevron.right").foregroundStyle(ACTheme.secondaryText)
        }.padding(.vertical, 15) }.buttonStyle(.plain)
    }
}

struct PrivacyScreen: View {
    var body: some View {
        List {
            Section("On This iPhone") {
                Label("Audiobooks remain in private app storage", systemImage: "iphone")
                Label("Account tokens are stored in Keychain", systemImage: "key")
                Label("Audiobooks are excluded from iCloud backup", systemImage: "icloud.slash")
            }
            Section("Private Server") {
                Label("Fingerprints are checked before upload", systemImage: "number")
                Label("Transcripts are never returned to the phone", systemImage: "doc.badge.ellipsis")
                Label("Temporary uploads are deleted after processing", systemImage: "trash")
            }
        }
        .navigationTitle("Privacy")
        .acScreen()
    }
}

struct CloudConnectionScreen: View {
    @AppStorage("cloudBaseURL") private var baseURL = ""
    @State private var accessToken = ""
    @State private var saved = false

    var body: some View {
        Form {
            Section("Private Backend") {
                TextField("https://api.example.com", text: $baseURL)
                    .textInputAutocapitalization(.never)
                    .keyboardType(.URL)
                    .autocorrectionDisabled()
                SecureField("Access token", text: $accessToken)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                Button(saved ? "Saved" : "Save Connection") {
                    baseURL = baseURL.trimmingCharacters(in: .whitespacesAndNewlines)
                    CloudCredentialStore.saveToken(
                        accessToken.trimmingCharacters(in: .whitespacesAndNewlines)
                    )
                    saved = true
                }
                .foregroundStyle(ACTheme.accent)
            }
            Section {
                Text("The server address controls where fingerprint lookups and new scans are sent. Audio is uploaded only when the server does not already have a matching scan.")
                    .font(.footnote)
                    .foregroundStyle(ACTheme.secondaryText)
            }
        }
        .navigationTitle("Cloud Connection")
        .navigationBarTitleDisplayMode(.inline)
        .acScreen()
        .onAppear { accessToken = CloudCredentialStore.loadToken() }
        .onChange(of: accessToken) { saved = false }
        .onChange(of: baseURL) { saved = false }
    }
}
