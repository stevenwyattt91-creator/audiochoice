import SwiftUI

struct OnboardingScreen: View {
    @Binding var completed: Bool
    @State private var page = 0

    // Four steps, in the order someone actually meets them: get a book in, get a book in from a
    // computer, decide what it plays, then the reading edition. Each names the control it is talking
    // about, because a tour that describes a feature without saying where it lives is a tour someone
    // has to take twice.
    private let pages = [
        OnboardingPage(
            icon: "square.and.arrow.down",
            title: "Bring in a book you own",
            detail: "Tap Import and choose an audiobook file. It is copied into AudioChoice's "
                + "private storage on this device — nothing is uploaded unless a scan is needed for "
                + "that exact recording. MP3 and M4B work directly, and Audible AAX files are "
                + "converted here using your own account."),
        OnboardingPage(
            icon: "laptopcomputer.and.iphone",
            title: "Downloaded it on a computer?",
            detail: "Some audiobooks are easiest to get on a computer. Open the AudioChoice "
                + "transfer tool there and send the file straight to your phone — no cable, no "
                + "cloud drive. It arrives in Import like any other file."),
        OnboardingPage(
            icon: "checkmark.shield",
            title: "Choose what you hear",
            detail: "Each audiobook is scanned once, and you pick which kinds of content to "
                + "remove. Playback skips or mutes those moments. Open the shield in the player to "
                + "change your choices, and protect them with a PIN under Parental Controls if you "
                + "like."),
        OnboardingPage(
            icon: "book",
            title: "Read along, or be read to",
            detail: "Import an EPUB and it lands on the Ebooks shelf, opening in the reader "
                + "instead of the player. Adjust the text, follow along while it is read aloud, or "
                + "attach it to an audiobook you already own to read and listen together."),
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
