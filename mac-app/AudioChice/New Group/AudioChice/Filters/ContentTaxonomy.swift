import Foundation

enum ContentTaxonomy {
    static let version = "1.0"

    static let sexualContentCategoryID =
        UUID(uuidString: "10000000-0000-0000-0000-000000000001")!
    static let explicitSexGroupID =
        UUID(uuidString: "11000000-0000-0000-0000-000000000001")!
    static let impliedSexGroupID =
        UUID(uuidString: "11000000-0000-0000-0000-000000000002")!
    static let explicitSexEventID =
        UUID(uuidString: "11100000-0000-0000-0000-000000000001")!
    static let impliedSexEventID =
        UUID(uuidString: "11100000-0000-0000-0000-000000000002")!

    static let profanityCategoryID =
        UUID(uuidString: "20000000-0000-0000-0000-000000000001")!
    static let profanityGroupID =
        UUID(uuidString: "21000000-0000-0000-0000-000000000001")!
    static let profanityEventID =
        UUID(uuidString: "21100000-0000-0000-0000-000000000001")!

    static let graphicViolenceCategoryID =
        UUID(uuidString: "30000000-0000-0000-0000-000000000001")!
    static let graphicViolenceGroupID =
        UUID(uuidString: "31000000-0000-0000-0000-000000000001")!
    static let graphicViolenceEventID =
        UUID(uuidString: "31100000-0000-0000-0000-000000000001")!

    static let selfHarmCategoryID =
        UUID(uuidString: "40000000-0000-0000-0000-000000000001")!
    static let selfHarmGroupID =
        UUID(uuidString: "41000000-0000-0000-0000-000000000001")!
    static let selfHarmEventID =
        UUID(uuidString: "41100000-0000-0000-0000-000000000001")!

    private static let supportedMappings: Set<Mapping> = [
        Mapping(
            categoryID: sexualContentCategoryID,
            groupID: explicitSexGroupID,
            eventID: explicitSexEventID
        ),
        Mapping(
            categoryID: sexualContentCategoryID,
            groupID: impliedSexGroupID,
            eventID: impliedSexEventID
        ),
        Mapping(
            categoryID: profanityCategoryID,
            groupID: profanityGroupID,
            eventID: profanityEventID
        ),
        Mapping(
            categoryID: graphicViolenceCategoryID,
            groupID: graphicViolenceGroupID,
            eventID: graphicViolenceEventID
        ),
        Mapping(
            categoryID: selfHarmCategoryID,
            groupID: selfHarmGroupID,
            eventID: selfHarmEventID
        )
    ]

    static func supports(_ event: ScanEvent) -> Bool {
        supportedMappings.contains(
            Mapping(
                categoryID: event.categoryID,
                groupID: event.groupID,
                eventID: event.eventID
            )
        )
    }

    private struct Mapping: Hashable {
        var categoryID: UUID
        var groupID: UUID
        var eventID: UUID
    }
}
