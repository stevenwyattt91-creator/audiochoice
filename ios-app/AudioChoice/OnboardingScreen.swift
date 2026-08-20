import SwiftUI

struct OnboardingScreen: View {
    @Binding var completed: Bool
    @State private var page = 0

    private let pages = [
        OnboardingPage(icon: "headphones", title: "Listen Your Way", detail: "Import audiobooks you own and listen with a clean, focused player."),
        OnboardingPage(icon: "checkmark.shield", title: "Choose What You Hear", detail: "AudioChoice can skip or mute supported content events using your active filter profile."),
        OnboardingPage(icon: "lock.shield", title: "Designed for Privacy", detail: "Files stay in private app storage. The server checks fingerprints first and never returns transcripts to your phone.")
    ]

    var body: some View {
        VStack(spacing: 28) {
            Spacer()
            TabView(selection: $page) {
                ForEach(Array(pages.enumerated()), id: \.offset) { index, item in
                    VStack(spacing: 24) {
                        Image(systemName: item.icon)
                            .font(.system(size: 74, weight: .light))
                            .foregroundStyle(ACTheme.accent)
                        Text(item.title).font(.largeTitle.bold()).multilineTextAlignment(.center)
                        Text(item.detail).font(.title3).foregroundStyle(ACTheme.secondaryText)
                            .multilineTextAlignment(.center).padding(.horizontal)
                    }
                    .tag(index)
                }
            }
            .tabViewStyle(.page(indexDisplayMode: .always))

            Button(page == pages.count - 1 ? "Get Started" : "Continue") {
                if page < pages.count - 1 {
                    withAnimation { page += 1 }
                } else {
                    completed = true
                }
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .tint(ACTheme.accent)
            .foregroundStyle(.black)
            .padding(.horizontal, 36)
            .frame(maxWidth: .infinity)
            Spacer().frame(height: 24)
        }
        .padding()
        .background(ACTheme.background.ignoresSafeArea())
    }
}

private struct OnboardingPage {
    let icon: String
    let title: String
    let detail: String
}
