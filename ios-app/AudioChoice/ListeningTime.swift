import Foundation

/// How long a book will really take, as distinct from how long it is.
///
/// Deliberately a mirror of Android's `ListeningTime`, including the treatment of a
/// non-positive rate, so the two platforms cannot disagree about the same book.
enum ListeningTime {

    /// Wall-clock seconds left in a book with `remainingBookSeconds` of audio still to play at
    /// `rate`.
    ///
    /// The player's remaining figure used to be the book's own remaining length, which at 1.5x
    /// counted down half again as fast as the clock: a listener could see the number falling
    /// quicker, but it never told them when they would actually finish. Dividing by the rate
    /// makes it answer the question being asked of it, and changes the moment the speed does.
    ///
    /// A rate of zero or less cannot be listened at, so it reads as normal speed rather than
    /// being divided by. Nothing in the app can set one -- `storedRate` clamps -- but the value
    /// outlives the build that wrote it, and an infinite time remaining would be a poor way to
    /// discover a stored rate had been corrupted.
    static func remainingRealSeconds(remainingBookSeconds: Double, rate: Float) -> Double {
        guard remainingBookSeconds.isFinite, remainingBookSeconds > 0 else { return 0 }
        let divisor = rate > 0 ? Double(rate) : 1
        return remainingBookSeconds / divisor
    }
}
