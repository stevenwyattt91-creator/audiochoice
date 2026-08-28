import Foundation

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

    /// A report that something played which should have been removed.
    ///
    /// The position is the tap, not the passage: the window carries the look-back, so the
    /// server can locate the passage without the client guessing where it started.
    static func missedContent(
        fingerprint: BookFingerprint,
        position: Double,
        scannerVersion: String?,
        categoryID: UUID? = nil
    ) -> FilterReportRequest {
        FilterReportRequest(
            fingerprint: fingerprint,
            kind: .missedContent,
            positionSeconds: max(position, 0),
            windowSeconds: lookBackSeconds,
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
