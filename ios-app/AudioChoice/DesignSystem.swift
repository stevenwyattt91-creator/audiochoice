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
    var isFinished = false

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
                    .clipped()
            }
        }
        .clipped()
        .clipShape(RoundedRectangle(cornerRadius: compact ? 10 : 16))
        .overlay {
            RoundedRectangle(cornerRadius: compact ? 10 : 16)
                .stroke(Color.white.opacity(0.14))
        }
        .overlay(alignment: .topTrailing) {
            if isFinished { FinishedBadge(compact: compact) }
        }
    }
}

/// Marks a finished book on its cover.
///
/// Sits on artwork of any colour, so the tick gets a dark disc behind it rather than
/// relying on contrast with the image. Carries the accessibility label because the cover
/// itself only announces the title, and a colour alone would say nothing to VoiceOver.
struct FinishedBadge: View {
    var compact = false

    var body: some View {
        Image(systemName: "checkmark.circle.fill")
            .font(.system(size: compact ? 17 : 24, weight: .bold))
            .symbolRenderingMode(.palette)
            .foregroundStyle(ACTheme.accent, Color.black.opacity(0.75))
            .padding(compact ? 5 : 8)
            .accessibilityLabel("Finished")
    }
}

extension View {
    func acScreen() -> some View {
        scrollContentBackground(.hidden)
            .background(ACTheme.background.ignoresSafeArea())
            .tint(ACTheme.accent)
    }
}

/// A text field wrapped in the app's own chrome.
///
/// Exists because the sign-in screen was a `Form`: bare `TextField`s on system-styled list rows,
/// which on this near-black theme read as unstyled text with no indication of where a field began,
/// which one was active, or that it was a control at all. Every other surface in AudioChoice is an
/// `ACCard` on `ACTheme.background`, so sign-in looked like a different app -- and it is the first
/// screen anyone sees.
///
/// The focus ring is the point. A field that does not visibly respond to being tapped is the single
/// clearest tell of an unfinished app, and it is also the thing that makes a password field feel
/// unsafe to type into.
struct ACField<Field: View>: View {
    var icon: String
    var isFocused: Bool
    /// Drawn in place of the trailing edge when supplied -- a password reveal toggle, a validation
    /// tick -- so the control sits inside the field rather than beside it.
    var accessory: AnyView?
    @ViewBuilder var field: Field

    init(
        icon: String,
        isFocused: Bool,
        accessory: AnyView? = nil,
        @ViewBuilder field: () -> Field
    ) {
        self.icon = icon
        self.isFocused = isFocused
        self.accessory = accessory
        self.field = field()
    }

    var body: some View {
        HStack(spacing: 13) {
            Image(systemName: icon)
                .font(.system(size: 16, weight: .medium))
                .foregroundStyle(isFocused ? ACTheme.accent : ACTheme.secondaryText)
                .frame(width: 21)
            field
                .font(.body)
                .foregroundStyle(.white)
                .tint(ACTheme.accent)
            if let accessory { accessory }
        }
        .padding(.horizontal, 16)
        .frame(height: 54)
        .background(ACTheme.panelRaised)
        .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .stroke(
                    isFocused ? ACTheme.accent.opacity(0.85) : ACTheme.border,
                    lineWidth: isFocused ? 1.6 : 1
                )
        }
        .animation(.easeOut(duration: 0.16), value: isFocused)
    }
}

/// The app's filled call to action: Subscribe, Sign In, Create Account.
///
/// A style rather than a copied modifier stack, because these buttons sit on three different
/// screens and had drifted apart -- different heights, corner radii and pressed behaviour on each,
/// which is what made the set feel assembled rather than designed.
struct ACPrimaryButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var isEnabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.headline)
            .frame(maxWidth: .infinity)
            .frame(height: 54)
            .background(
                // Dimmed rather than greyed when disabled: the accent still reads as the action
                // being waited on, where grey reads as an action that is gone.
                ACTheme.accent.opacity(isEnabled ? (configuration.isPressed ? 0.84 : 1) : 0.30)
            )
            .foregroundStyle(isEnabled ? .black : Color.black.opacity(0.55))
            .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
            .scaleEffect(configuration.isPressed ? 0.985 : 1)
            .animation(.easeOut(duration: 0.12), value: configuration.isPressed)
    }
}

/// The outlined companion to `ACPrimaryButtonStyle`, for an action of equal footing but lower
/// expectation -- redeeming a code beside subscribing.
struct ACSecondaryButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var isEnabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.headline)
            .frame(maxWidth: .infinity)
            .frame(height: 54)
            .background(ACTheme.accent.opacity(configuration.isPressed ? 0.14 : 0.07))
            .foregroundStyle(ACTheme.accent.opacity(isEnabled ? 1 : 0.4))
            .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
            .overlay {
                RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .stroke(ACTheme.accent.opacity(isEnabled ? 0.55 : 0.2), lineWidth: 1)
            }
            .scaleEffect(configuration.isPressed ? 0.985 : 1)
            .animation(.easeOut(duration: 0.12), value: configuration.isPressed)
    }
}

/// "or" between the email form and the third-party buttons, with a rule either side.
struct ACDivider: View {
    var label: String

    var body: some View {
        HStack(spacing: 14) {
            Rectangle().fill(ACTheme.border).frame(height: 1)
            Text(label)
                .font(.footnote)
                .foregroundStyle(ACTheme.secondaryText)
            Rectangle().fill(ACTheme.border).frame(height: 1)
        }
    }
}
