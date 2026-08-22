import Foundation

enum FilterPreviewFactory {

    static func makePreviewProfile() -> FilterProfile {

        FilterProfile(
            categories: [

                FilterCategory(
                    displayName: "Sexual Content",
                    groups: [

                        FilterGroup(
                            displayName: "Explicit Sex Scenes",
                            events: [

                                FilterEvent(
                                    title: "Extended intimate encounter",
                                    displaySummary: "Two romantic partners engage in explicit intimacy.",
                                    startTime: 420,
                                    endTime: 650,
                                    severity: .explicit
                                ),

                                FilterEvent(
                                    title: "Explicit encounter",
                                    displaySummary: "Extended intimate encounter.",
                                    startTime: 1200,
                                    endTime: 1400,
                                    severity: .explicit
                                )
                            ]
                        ),

                        FilterGroup(
                            displayName: "Implied Sexual Content",
                            events: [

                                FilterEvent(
                                    title: "Romantic bedroom scene",
                                    displaySummary: "Couple kisses before fading to black.",
                                    startTime: 2500,
                                    endTime: 2558,
                                    severity: .mild
                                )
                            ]
                        )
                    ]
                ),

                FilterCategory(
                    displayName: "Profanity",
                    groups: [

                        FilterGroup(
                            displayName: "F-word",
                            events: [

                                FilterEvent(
                                    title: "F-word",
                                    displaySummary: "Strong profanity.",
                                    startTime: 320,
                                    endTime: 321,
                                    action: .mute,
                                    severity: .strong
                                ),

                                FilterEvent(
                                    title: "F-word",
                                    displaySummary: "Strong profanity.",
                                    startTime: 5100,
                                    endTime: 5101,
                                    action: .mute,
                                    severity: .strong
                                )
                            ]
                        )
                    ]
                )
            ]
        )
    }
}
