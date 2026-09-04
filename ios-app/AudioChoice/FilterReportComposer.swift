import Foundation

/// How far back a refined report reaches, as a labelled choice rather than free entry.
///
/// A listener choosing this has no reason to know the server's own limit, so the choices
/// stop at it (FilterReportComposer.maximumWindowSeconds) rather than offering one that
/// would be silently clamped. Mirrors Android's FilterReportTimeframe.
enum FilterReportTimeframe: CaseIterable, Identifiable {
    case justThisMoment, halfMinute, oneMinute, twoMinutes

    var id: Self { self }

    var seconds: Double {
        switch self {
        case .justThisMoment: FilterReportComposer.lookBackSeconds
        case .halfMinute: 30
        case .oneMinute: 60
        case .twoMinutes: FilterReportComposer.maximumWindowSeconds
        }
    }

    var title: String {
        switch self {
        case .justThisMoment: "Just this moment (20s)"
        case .halfMinute: "Last 30 seconds"
        case .oneMinute: "Last minute"
        case .twoMinutes: "Last 2 minutes"
        }
    }
}

/// Turns a moment in a book into a report.
///
/// Kept apart from the queue and the player so the part that decides *what* a report says
/// can be checked without a network client or an audio session.
enum FilterReportComposer {
    /// How much audio before the tap a report covers.
    ///
    /// Matches the server's default. A listener has to hear the passage, realise it should
    /// not have played, find the button and tap, and twenty seconds covers that without
    /// sweeping in so much that triage cannot tell what was meant.
    static let lookBackSeconds: Double = 20

    /// Longer than this describes the book rather than a moment in it. Matches the server's
    /// own clamp, so a choice offered here is never silently narrowed after submission.
    static let maximumWindowSeconds: Double = 120

    /// A report that something played which should have been removed.
    ///
    /// The position is the tap, not the passage: the window carries the look-back, so the
    /// server can locate the passage without the client guessing where it started.
    static func missedContent(
        fingerprint: BookFingerprint,
        position: Double,
        scannerVersion: String?,
        categoryID: UUID? = nil,
        windowSeconds: Double = lookBackSeconds
    ) -> FilterReportRequest {
        FilterReportRequest(
            fingerprint: fingerprint,
            kind: .missedContent,
            positionSeconds: max(position, 0),
            windowSeconds: min(max(windowSeconds, 1), maximumWindowSeconds),
            scannerVersion: scannerVersion,
            scanEventID: nil,
            categoryID: categoryID
        )
    }

    /// A report that a skip removed something it should not have.
    ///
    /// Carries the event, which is what makes this actionable: it names the exact control
    /// that fired rather than leaving a timestamp to be matched back to one. The position
    /// is the flagged range's own start, and the window covers it, because the listener is
    /// already past it by the time the skip is visible.
    static func wronglyFiltered(
        fingerprint: BookFingerprint,
        event: ScanEvent,
        scannerVersion: String?
    ) -> FilterReportRequest {
        let span = max(event.endTime - event.startTime, 1)
        return FilterReportRequest(
            fingerprint: fingerprint,
            kind: .wronglyFiltered,
            positionSeconds: max(event.startTime, 0),
            windowSeconds: min(span, 120),
            scannerVersion: scannerVersion,
            scanEventID: event.id,
            categoryID: event.categoryID
        )
    }
}
