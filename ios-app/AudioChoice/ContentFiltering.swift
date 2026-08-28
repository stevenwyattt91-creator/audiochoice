import Foundation

enum FilterCategory: String, CaseIterable, Identifiable, Codable {
    case sexualContent
    case profanity
    case graphicViolence
    case drugsAndAlcohol
    case blasphemy
    case selfHarm

    var id: String { rawValue }

    var title: String {
        switch self {
        case .sexualContent: "Sexual Content"
        case .profanity: "Profanity"
        case .graphicViolence: "Graphic Violence"
        case .drugsAndAlcohol: "Drugs & Alcohol"
        case .blasphemy: "Blasphemy"
        case .selfHarm: "Self-Harm"
        }
    }

    var icon: String {
        switch self {
        case .sexualContent: "heart.slash"
        case .profanity: "text.badge.checkmark"
        case .graphicViolence: "shield"
        case .drugsAndAlcohol: "pills"
        case .blasphemy: "quote.bubble"
        case .selfHarm: "cross.case"
        }
    }
}

enum IOSContentTaxonomy {
    private static let sexual = UUID(uuidString: "10000000-0000-0000-0000-000000000001")!
    private static let profanity = UUID(uuidString: "20000000-0000-0000-0000-000000000001")!
    private static let violence = UUID(uuidString: "30000000-0000-0000-0000-000000000001")!
    private static let substances = UUID(uuidString: "40000000-0000-0000-0000-000000000001")!
    private static let blasphemy = UUID(uuidString: "50000000-0000-0000-0000-000000000001")!
    private static let selfHarm = UUID(uuidString: "60000000-0000-0000-0000-000000000001")!

    /// The identifier the server uses for a category, for tagging a listener's report.
    static func categoryID(for category: FilterCategory) -> UUID {
        switch category {
        case .sexualContent: sexual
        case .profanity: profanity
        case .graphicViolence: violence
        case .drugsAndAlcohol: substances
        case .blasphemy: blasphemy
        case .selfHarm: selfHarm
        }
    }

    /// Which broad category an event belongs to, for labelling only.
    ///
    /// What actually gets filtered is decided by PlaybackFilterTaxonomy and the book's
    /// own settings, not here.
    static func category(for event: ScanEvent) -> FilterCategory? {
        switch event.categoryID {
        case sexual: .sexualContent
        case profanity: .profanity
        case violence: .graphicViolence
        case substances: .drugsAndAlcohol
        case blasphemy: .blasphemy
        case selfHarm: .selfHarm
        default: nil
        }
    }
}

/// Whether playback can currently enforce a book's filters.
///
/// Mirrors the Android client's FilterAvailability. The distinction that matters is
/// between "this book has nothing to filter" and "this book's filter data is missing",
/// which look identical from an empty event list.
enum FilterAvailability {
    /// A scan is queued or running, so filter data is expected shortly.
    case loading
    /// Filter data is present and being enforced.
    case available
    /// No filter data, and none pending. Nothing is being filtered.
    case unavailable

    static func of(_ record: LibraryBookRecord?) -> FilterAvailability {
        guard let record else { return .unavailable }
        if record.scanResult != nil { return .available }
        return record.pendingScanID != nil ? .loading : .unavailable
    }
}
