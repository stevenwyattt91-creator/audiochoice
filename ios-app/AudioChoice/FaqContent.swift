import Foundation

/// The in-app help content, served rather than compiled into the app.
///
/// Both apps used to hold their own hardcoded copy and they drifted: Android carried eleven questions
/// and iOS four different ones, so the same product answered differently depending on the phone.
/// Neither mentioned the reading edition, the two library shelves, the voice tiers, rescanning an
/// audiobook, or password reset.
///
/// Serving it takes an App Store review out of the path of correcting a wrong answer, which is what let
/// it go stale. A bundled copy remains as a fallback: a help screen that is empty when the network is
/// poor is worse than one slightly behind.
struct FaqResponse: Decodable {
    var version: Int = 0
    var sections: [FaqSection] = []
}

struct FaqSection: Decodable, Identifiable {
    var title: String = ""
    var items: [FaqEntry] = []
    /// Titles are unique within the content, and stable, which is what a list needs.
    var id: String { title }
}

struct FaqEntry: Decodable, Identifiable {
    var question: String = ""
    var answer: String = ""
    var id: String { question }
}

enum FaqLoader {
    /// Fetches the served content, or nil when it cannot be had.
    ///
    /// Unauthenticated: it is help text about nobody, and the screen has to work before someone can
    /// sign in as much as after -- which matters most for the answer about not being able to sign in.
    static func fetch() async -> FaqResponse? {
        guard let base = baseURL() else { return nil }
        var request = URLRequest(url: base.appendingPathComponent("v1").appendingPathComponent("faq"))
        request.httpMethod = "GET"
        // Short: this is a help screen with usable content already on it, so waiting is worse than
        // showing the bundled answers now.
        request.timeoutInterval = 8
        guard let (data, response) = try? await URLSession.shared.data(for: request),
              let http = response as? HTTPURLResponse,
              (200..<300).contains(http.statusCode),
              let decoded = try? JSONDecoder().decode(FaqResponse.self, from: data)
        else { return nil }
        return decoded
    }

    private static func baseURL() -> URL? {
        let bundled = (Bundle.main.object(forInfoDictionaryKey: "AudioChoiceAPIBaseURL") as? String)?
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let saved = UserDefaults.standard.string(forKey: "cloudBaseURL")?
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let address = bundled.isEmpty ? saved : bundled
        return address.isEmpty ? nil : URL(string: address)
    }

    /// The copy that ships with the app.
    ///
    /// Deliberately short. It exists so the screen is never empty, not to be a second source of truth
    /// that drifts from the server the way the two apps' hardcoded copies drifted from each other.
    static let bundled = FaqResponse(version: 1, sections: [
        FaqSection(title: "Getting your audiobooks in", items: [
            FaqEntry(
                question: "Where can I get audiobooks I can import?",
                answer: "Any audiobook you own as a file will work. Stores selling DRM-free "
                    + "downloads, such as Libro.fm, are simplest: download the file and import it."),
            FaqEntry(
                question: "Which file types work?",
                answer: "MP3 and M4B are the usual ones. Audible AAX files can be converted on the "
                    + "device using your own account's activation. EPUB files are imported as "
                    + "reading editions rather than audiobooks."),
        ]),
        FaqSection(title: "Filters", items: [
            FaqEntry(
                question: "How do filters work?",
                answer: "An audiobook is scanned once, and you choose which categories to remove. "
                    + "Playback skips or mutes those moments."),
            FaqEntry(
                question: "Why does one audiobook say filters are unavailable?",
                answer: "Filter results belong to one exact recording, so a different edition needs "
                    + "its own scan. Open the player and tap \"Scan this audiobook\"."),
        ]),
        FaqSection(title: "Your account", items: [
            FaqEntry(
                question: "I cannot sign in on a new device.",
                answer: "Your account works on every device. If the password is not accepted, "
                    + "choose \"Forgot password\" and we will email a six-digit code."),
        ]),
    ])
}
