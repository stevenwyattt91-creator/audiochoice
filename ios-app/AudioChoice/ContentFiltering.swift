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

struct FilterEventGroup: Identifiable {
    let id: UUID
    let title: String
    let events: [ScanEvent]
}

struct FilterEventCategoryGroup: Identifiable {
    let id: UUID
    let title: String
    let icon: String
    let groups: [FilterEventGroup]
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
        if let aggregateDisplay = event.aggregateDisplay, !aggregateDisplay.isEmpty {
            return aggregateDisplay
        }
        if let safeDescription = event.safeDescription, !safeDescription.isEmpty {
            return safeDescription
        }
        if event.eventID == explicit { return "Explicit sexual content" }
        return category(for: event)?.title ?? "Unsupported event"
    }

    static func userFacingEvents(_ events: [ScanEvent]) -> [ScanEvent] {
        var aggregateKeys = Set<String>()
        return events.sorted { $0.startTime < $1.startTime }.filter { event in
            guard shouldSkip(event) else { return false }
            guard let key = event.aggregateKey, !key.isEmpty else { return true }
            return aggregateKeys.insert(key).inserted
        }
    }

    static func shouldSkip(_ event: ScanEvent) -> Bool {
        guard let category = category(for: event) else { return false }
        guard category == .graphicViolence else { return true }
        // The scanner can retain lower-severity violence for internal review,
        // but playback only skips the severe Android-visible groups.
        let severeViolenceGroups: Set<String> = [
            "31000000-0000-0000-0000-000000000003", // graphic violence / gore
            "31000000-0000-0000-0000-000000000004"  // torture
        ]
        return severeViolenceGroups.contains(event.groupID.uuidString.uppercased())
    }

    static func controlCount(_ events: [ScanEvent]) -> Int {
        userFacingEvents(events).count
    }

    static func hierarchy(for events: [ScanEvent]) -> [FilterEventCategoryGroup] {
        let visible = userFacingEvents(events)
        let byCategory = Dictionary(grouping: visible) { $0.categoryID }
        return FilterCategory.allCases.compactMap { category in
            guard let categoryID = categoryID(for: category), let categoryEvents = byCategory[categoryID] else { return nil }
            let groups = Dictionary(grouping: categoryEvents) { $0.groupID }
                .map { groupID, values in
                    FilterEventGroup(
                        id: groupID,
                        title: groupTitle(for: groupID) ?? "Other detected events",
                        events: values.sorted { $0.startTime < $1.startTime }
                    )
                }
                .sorted { $0.events.first?.startTime ?? 0 < $1.events.first?.startTime ?? 0 }
            return FilterEventCategoryGroup(
                id: categoryID,
                title: category == .graphicViolence ? "Violence" : category.title,
                icon: category.icon,
                groups: groups
            )
        }
    }

    private static func categoryID(for category: FilterCategory) -> UUID? {
        switch category {
        case .sexualContent: sexual
        case .profanity: profanity
        case .graphicViolence: violence
        case .drugsAndAlcohol: substances
        case .blasphemy: blasphemy
        case .selfHarm: selfHarm
        }
    }

    private static func groupTitle(for id: UUID) -> String? {
        let groups: [String: String] = [
            "11000000-0000-0000-0000-000000000001": "Suggestive dialogue",
            "11000000-0000-0000-0000-000000000002": "Sexual references",
            "11000000-0000-0000-0000-000000000003": "Nudity",
            "11000000-0000-0000-0000-000000000004": "Implied sexual activity",
            "11000000-0000-0000-0000-000000000005": "Explicit sexual activity",
            "11000000-0000-0000-0000-000000000006": "Complete sex scenes",
            "21000000-0000-0000-0000-000000000001": "Mild profanity",
            "21000000-0000-0000-0000-000000000002": "Strong profanity",
            "21000000-0000-0000-0000-000000000003": "Sexual profanity",
            "21000000-0000-0000-0000-000000000004": "Slurs / derogatory language",
            "31000000-0000-0000-0000-000000000003": "Graphic violence / gore",
            "31000000-0000-0000-0000-000000000004": "Torture",
            "31000000-0000-0000-0000-000000000006": "Violence involving children",
            "31000000-0000-0000-0000-000000000007": "Violence involving animals",
            "41000000-0000-0000-0000-000000000001": "Alcohol use",
            "41000000-0000-0000-0000-000000000002": "Intoxication",
            "41000000-0000-0000-0000-000000000003": "Drug references",
            "41000000-0000-0000-0000-000000000004": "Drug use",
            "41000000-0000-0000-0000-000000000005": "Drug abuse / overdose",
            "51000000-0000-0000-0000-000000000001": "Religious profanity",
            "51000000-0000-0000-0000-000000000002": "Blasphemous statements",
            "61000000-0000-0000-0000-000000000001": "Self-harm references",
            "61000000-0000-0000-0000-000000000002": "Suicidal thoughts",
            "61000000-0000-0000-0000-000000000003": "Suicide attempt",
            "61000000-0000-0000-0000-000000000004": "Depiction of self-harm / suicide"
        ]
        return groups[id.uuidString.uppercased()]
    }
}

enum FilterPreferences {
    static func isEnabled(_ category: FilterCategory) -> Bool { true }
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
