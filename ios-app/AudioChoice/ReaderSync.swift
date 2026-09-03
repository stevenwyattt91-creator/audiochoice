import Foundation

/// A private timing-to-character range. It intentionally carries no transcript text.
struct ReaderTimingRange: Codable, Equatable {
    let startTime: Double
    let endTime: Double
    let startCharacter: Int
    let endCharacter: Int
}

/// Maps between audio time and reading position using the server's alignment.
///
/// Coverage is deliberately sparse: the server skips any transcript segment it cannot
/// confidently anchor in the EPUB, so the ranges are neither contiguous nor complete.
/// Both directions therefore return nil rather than guessing, and the caller keeps its
/// previous state across a gap instead of snapping to the wrong place.
///
/// Ranges are ordered by both time and character, because the server walks the transcript
/// with a monotonic cursor.
enum ReaderSync {
    private static let minimumDuration = 0.001

    /// Character offset currently being narrated, or nil if no range covers `seconds`.
    /// Roughly where the listening has reached, for a book with no alignment.
    ///
    /// A straight proportion of the text: two hours into a ten-hour book lands a fifth of the way
    /// in. Wrong by pages, and deliberately offered anyway. A book whose EPUB was never aligned --
    /// and plenty were not, because alignment is a separate request that can fail while filters
    /// succeed -- otherwise opens at the title page ten hours in, with nothing to search for. Being
    /// approximately right is the difference between a starting point and no way back at all.
    ///
    /// Nil when the numbers cannot support even that: no duration, no text, or nothing played.
    static func approximateCharacter(
        atSeconds seconds: Double,
        duration: Double,
        characterCount: Int
    ) -> Int? {
        guard seconds > 0, duration > 0, characterCount > 0, seconds.isFinite, duration.isFinite
        else { return nil }
        let fraction = min(max(seconds / duration, 0), 1)
        return Int(fraction * Double(characterCount))
    }

    static func character(at seconds: Double, in timings: [ReaderTimingRange]) -> Int? {
        guard let timing = timingContaining(seconds, in: timings) else { return nil }
        let duration = Swift.max(timing.endTime - timing.startTime, minimumDuration)
        let fraction = Swift.min(Swift.max((seconds - timing.startTime) / duration, 0), 1)
        let length = timing.endCharacter - timing.startCharacter
        return timing.startCharacter + Int(Double(length) * fraction)
    }

    /// Audio time for a character offset, for tap-to-seek.
    ///
    /// Falls forward to the next aligned range when the tapped text has no timing of its
    /// own, so tapping an unaligned paragraph still moves the audio somewhere sensible
    /// rather than doing nothing.
    static func time(forCharacter character: Int, in timings: [ReaderTimingRange]) -> Double? {
        if timings.isEmpty { return nil }
        if let containing = timings.first(where: {
            character >= $0.startCharacter && character < $0.endCharacter
        }) {
            let length = Swift.max(containing.endCharacter - containing.startCharacter, 1)
            let offset = Double(character - containing.startCharacter) / Double(length)
            let fraction = Swift.min(Swift.max(offset, 0), 1)
            return containing.startTime + (containing.endTime - containing.startTime) * fraction
        }
        return timings.first { $0.startCharacter >= character }?.startTime
    }

    private static func timingContaining(
        _ seconds: Double,
        in timings: [ReaderTimingRange]
    ) -> ReaderTimingRange? {
        var low = 0
        var high = timings.count - 1
        while low <= high {
            let middle = (low + high) / 2
            let timing = timings[middle]
            if seconds < timing.startTime {
                high = middle - 1
            } else if seconds >= timing.endTime {
                low = middle + 1
            } else {
                return timing
            }
        }
        return nil
    }
}
