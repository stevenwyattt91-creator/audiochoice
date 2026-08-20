import GoogleSignIn
import SwiftUI

@main
struct AudioChoiceApp: App {
    @AppStorage("onboardingCompleted") private var onboardingCompleted = false

    var body: some Scene {
        WindowGroup {
            Group {
                if onboardingCompleted {
                    RootTabView()
                } else {
                    OnboardingScreen(completed: $onboardingCompleted)
                }
                }
                .preferredColorScheme(.dark)
                .onOpenURL { url in
                    GIDSignIn.sharedInstance.handle(url)
                }
        }
    }
}
