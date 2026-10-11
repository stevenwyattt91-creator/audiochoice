import Foundation

// Sexual content only, permanently -- see the backend's ContentTaxonomy.EnforcedLabels
// remarks. Profanity, Graphic Violence, Drugs & Alcohol, Blasphemy, and Self-Harm are
// removed from this picker, matching Android's identical reduction of
// FilterReportCategory: this deployment never scans for any of them, so there is nothing
// for a report about one to do.
enum FilterCategory: String, CaseIterable, Identifiable, Codable {
    case sexualContent

    var id: String { rawValue }

    var title: String {
        switch self {
        case .sexualContent: "Sexual Content"
        }
    }

    var icon: String {
        switch self {
        case .sexualContent: "heart.slash"
        }
    }
}

enum IOSContentTaxonomy {
    private static let sexual = UUID(uuidString: "10000000-0000-0000-0000-000000000001")!

    /// The identifier the server uses for a category, for tagging a listener's report.
    static func categoryID(for category: FilterCategory) -> UUID {
        switch category {
        case .sexualContent: sexual
        }
    }

    /// Which broad category an event belongs to, for labelling only.
    ///
    /// What actually gets filtered is decided by PlaybackFilterTaxonomy and the book's
    /// own settings, not here.
    static func category(for event: ScanEvent) -> FilterCategory? {
        switch event.categoryID {
        case sexual: .sexualContent
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
