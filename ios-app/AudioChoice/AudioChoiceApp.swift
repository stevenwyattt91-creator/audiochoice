import GoogleSignIn
import SwiftUI

@main
struct AudioChoiceApp: App {
    @AppStorage("onboardingCompleted") private var onboardingCompleted = false
    @StateObject private var authSession = AuthSession.shared
    @StateObject private var companionTransfers = CompanionTransferCoordinator.shared

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
        }
    }
}
