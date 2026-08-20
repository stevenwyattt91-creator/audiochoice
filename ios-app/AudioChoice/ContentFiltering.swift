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

enum FilterBehavior: String, CaseIterable, Identifiable {
    case skip, mute
    var id: String { rawValue }
    var title: String { self == .skip ? "Skip passage" : "Mute passage" }
}

enum IOSContentTaxonomy {
    private static let sexual = UUID(uuidString: "10000000-0000-0000-0000-000000000001")!
    private static let profanity = UUID(uuidString: "20000000-0000-0000-0000-000000000001")!
    private static let violence = UUID(uuidString: "30000000-0000-0000-0000-000000000001")!
    private static let substances = UUID(uuidString: "40000000-0000-0000-0000-000000000001")!
    private static let blasphemy = UUID(uuidString: "50000000-0000-0000-0000-000000000001")!
    private static let selfHarm = UUID(uuidString: "60000000-0000-0000-0000-000000000001")!
    private static let explicit = UUID(uuidString: "11100000-0000-0000-0000-000000000001")!

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

    static func detail(for event: ScanEvent) -> String {
        if event.eventID == explicit { return "Explicit sexual content" }
        return category(for: event)?.title ?? "Unsupported event"
    }
}

enum FilterPreferences {
    static func isEnabled(_ category: FilterCategory) -> Bool {
        let key = "filterEnabled.\(category.rawValue)"
        if UserDefaults.standard.object(forKey: key) == nil {
        return category == .sexualContent || category == .profanity
        }
        return UserDefaults.standard.bool(forKey: key)
    }

    static var behavior: FilterBehavior {
        FilterBehavior(rawValue: UserDefaults.standard.string(forKey: "filterBehavior") ?? "skip") ?? .skip
    }
}

struct FilterCorrection: Codable, Identifiable {
    let id: UUID
    let bookID: UUID
    let scanEventID: UUID
    let reportedAt: Date
}

enum FilterCorrectionStore {
    private static let key = "filterCorrections.v1"

    static func report(bookID: UUID, eventID: UUID) {
        var values = load()
        guard !values.contains(where: { $0.bookID == bookID && $0.scanEventID == eventID }) else { return }
        values.append(FilterCorrection(id: UUID(), bookID: bookID, scanEventID: eventID, reportedAt: Date()))
        if let data = try? JSONEncoder().encode(values) { UserDefaults.standard.set(data, forKey: key) }
    }

    static func contains(bookID: UUID, eventID: UUID) -> Bool {
        load().contains { $0.bookID == bookID && $0.scanEventID == eventID }
    }

    private static func load() -> [FilterCorrection] {
        guard let data = UserDefaults.standard.data(forKey: key) else { return [] }
        return (try? JSONDecoder().decode([FilterCorrection].self, from: data)) ?? []
    }
}
