import SwiftUI
import UIKit

enum ACTheme {
    static let background = Color(red: 0.025, green: 0.035, blue: 0.035)
    static let panel = Color(red: 0.065, green: 0.08, blue: 0.08)
    static let panelRaised = Color(red: 0.085, green: 0.105, blue: 0.10)
    static let accent = Color(red: 0.43, green: 0.78, blue: 0.20)
    static let secondaryText = Color.white.opacity(0.58)
    static let border = Color.white.opacity(0.10)
}

struct ACCard<Content: View>: View {
    @ViewBuilder var content: Content

    var body: some View {
        content
            .padding(16)
            .background(ACTheme.panel)
            .clipShape(RoundedRectangle(cornerRadius: 18, style: .continuous))
            .overlay {
                RoundedRectangle(cornerRadius: 18, style: .continuous)
                    .stroke(ACTheme.border, lineWidth: 1)
            }
    }
}

struct BookCover: View {
    var title: String
    var compact = false
    var artworkFileName: String? = nil

    var body: some View {
        ZStack {
            LinearGradient(
                colors: [
                    Color(red: 0.08, green: 0.17, blue: 0.18),
                    Color(red: 0.28, green: 0.20, blue: 0.10),
                    Color.black
                ],
                startPoint: .topLeading,
                endPoint: .bottomTrailing
            )

            Image(systemName: "building.columns.fill")
                .font(.system(size: compact ? 28 : 70))
                .foregroundStyle(.white.opacity(0.30))

            Text(title)
                .font(compact ? .caption.bold() : .title2.bold())
                .multilineTextAlignment(.center)
                .padding(10)
                .frame(maxHeight: .infinity, alignment: .bottom)

            if let artworkFileName,
               let image = UIImage(contentsOfFile: AudiobookImportService.artworkURL(fileName: artworkFileName).path) {
                Image(uiImage: image)
                    .resizable()
                    .scaledToFill()
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: compact ? 10 : 16))
        .overlay {
            RoundedRectangle(cornerRadius: compact ? 10 : 16)
                .stroke(Color.white.opacity(0.14))
        }
    }
}

extension View {
    func acScreen() -> some View {
        scrollContentBackground(.hidden)
            .background(ACTheme.background.ignoresSafeArea())
            .tint(ACTheme.accent)
    }
}
