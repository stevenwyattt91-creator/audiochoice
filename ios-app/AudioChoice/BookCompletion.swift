import Foundation

/// When an audiobook counts as finished.
///
/// Reaching the exact final sample almost never happens: books end with credits, an
/// outro, or a few seconds of silence, and listeners stop before that. Requiring the
/// very end would mean nothing was ever marked complete.
enum BookCompletion {
    /// How close to the end still counts as finished, for a book long enough for it to
    /// be a small fraction of the whole.
    static let remainingSeconds: Double = 30

    /// The fallback for short files, where 30 seconds could be most of the runtime, or
    /// even a negative threshold that marked them complete the moment they opened.
    static let completedFraction: Double = 0.98

    static func isComplete(position: Double, duration: Double) -> Bool {
        // Duration is 0 until an asset finishes loading. Treating that as complete would
        // mark a book finished simply for having been opened.
        guard duration > 0, position > 0 else { return false }
        return position >= threshold(duration: duration)
    }

    static func threshold(duration: Double) -> Double {
        guard duration > 0 else { return .greatestFiniteMagnitude }
        return max(duration - remainingSeconds, duration * completedFraction)
    }
}
