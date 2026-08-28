import GoogleSignIn
import SwiftUI

@main
struct AudioChoiceApp: App {
    @AppStorage("onboardingCompleted") private var onboardingCompleted = false
    @StateObject private var authSession = AuthSession.shared
    @StateObject private var companionTransfers = CompanionTransferCoordinator.shared
    @Environment(\.scenePhase) private var scenePhase

    init() {
        // Runs before any screen can read the lock, so a PIN set by an earlier build keeps
        // working and its plaintext copy stops existing.
        ParentalPinStore.migrateLegacyPinIfNeeded()
    }

    var body: some Scene {
        WindowGroup {
            Group {
                if authSession.user == nil {
                    NavigationStack {
                        AccountScreen(isLaunchScreen: true)
                    }
                } else if onboardingCompleted {
                    RootTabView()
                } else {
                    OnboardingScreen(completed: $onboardingCompleted)
                }
                }
                .preferredColorScheme(.dark)
                .onOpenURL { url in
                    if !GIDSignIn.sharedInstance.handle(url) {
                        companionTransfers.receive(url)
                    }
                }
                // Reports are written to disk the instant they are tapped, which is what
                // makes reporting work in a car with no signal. They only leave the device
                // here, on launch and whenever the app comes back to the foreground.
                .task { await FilterReportQueue.shared.flush() }
                .onChange(of: scenePhase) { _, phase in
                    guard phase == .active else { return }
                    Task { await FilterReportQueue.shared.flush() }
                }
        }
    }
}
