import SwiftUI

enum ReaderTheme: String, Codable, CaseIterable, Identifiable {
    case light, sepia, dark

    var id: String { rawValue }
    var title: String { rawValue.capitalized }
}

/// Typeface for the reading edition.
///
/// `openDyslexic` is the same typeface Kindle offers, for the same reason: weighted letter
/// bottoms and deliberately asymmetric shapes make it harder to rotate or transpose
/// similar characters. Bundled from the SIL Open Font License release, with the licence in
/// Resources/OpenDyslexic-OFL.txt as the OFL requires.
enum ReaderFont: String, Codable, CaseIterable, Identifiable {
    case system
    case openDyslexic

    var id: String { rawValue }

    var title: String {
        switch self {
        case .system: "Standard"
        case .openDyslexic: "OpenDyslexic"
        }
    }

    /// OpenDyslexic sets a larger x-height with heavy letter bottoms, so lines sit visually
    /// closer than the same measurement in the default face.
    var lineHeightFactor: Double {
        switch self {
        case .system: 1
        case .openDyslexic: 1.12
        }
    }

    func font(size: Double) -> Font {
        switch self {
        case .system: .system(size: size, design: .serif)
        case .openDyslexic: .custom("OpenDyslexic", fixedSize: size)
        }
    }
}

/// Reading preferences.
///
/// Device-wide rather than per-book: a listener's preferred text size and theme should not
/// reset every time they open a different audiobook. Reading *position* is per-book and
/// lives in `ReaderStore`.
struct ReaderSettings: Codable, Equatable {
    var fontScale: Double = 1
    /// Sepia matches the paper palette the reader shipped with on Android.
    var theme: ReaderTheme = .sepia
    var marginScale: Double = 1
    var font: ReaderFont = .system
    /// Highlight and scroll the text to match audio position, and allow tapping a
    /// paragraph to seek. On by default: following along is the reason to pair a reading
    /// edition with an audiobook at all.
    var followAudio: Bool = true

    static let fontScales: [Double] = [0.85, 1, 1.15, 1.35]
    static let marginScales: [Double] = [0.6, 1, 1.5]

    static let baseFontSize: Double = 19
    static let baseLineSpacing: Double = 11
    static let baseMargin: Double = 22

    var fontSize: Double { Self.baseFontSize * fontScale }
    var lineSpacing: Double { Self.baseLineSpacing * fontScale * font.lineHeightFactor }
    var margin: Double { Self.baseMargin * marginScale }

    static func fontScaleLabel(_ scale: Double) -> String {
        switch scale {
        case ..<0.9: "Small"
        case ..<1.05: "Medium"
        case ..<1.2: "Large"
        default: "Extra large"
        }
    }

    static func marginScaleLabel(_ scale: Double) -> String {
        switch scale {
        case ..<0.8: "Narrow"
        case ..<1.2: "Normal"
        default: "Wide"
        }
    }

    // MARK: - Persistence

    private static let storageKey = "readerSettings.v1"

    static func load() -> ReaderSettings {
        guard let data = UserDefaults.standard.data(forKey: storageKey),
              let settings = try? JSONDecoder().decode(ReaderSettings.self, from: data) else {
            return ReaderSettings()
        }
        return settings
    }

    func save() {
        guard let data = try? JSONEncoder().encode(self) else { return }
        UserDefaults.standard.set(data, forKey: Self.storageKey)
    }
}

/// Paper and ink for a reading theme.
struct ReaderPalette {
    let paper: Color
    let ink: Color
    let mutedInk: Color

    static func of(_ theme: ReaderTheme) -> ReaderPalette {
        switch theme {
        case .light:
            ReaderPalette(
                paper: Color(white: 0.99),
                ink: Color(white: 0.09),
                mutedInk: Color(white: 0.42)
            )
        case .sepia:
            ReaderPalette(
                paper: Color(red: 0.98, green: 0.95, blue: 0.88),
                ink: Color(red: 0.20, green: 0.16, blue: 0.11),
                mutedInk: Color(red: 0.45, green: 0.39, blue: 0.31)
            )
        case .dark:
            ReaderPalette(
                paper: Color(white: 0.07),
                ink: Color(white: 0.88),
                mutedInk: Color(white: 0.58)
            )
        }
    }
}
