import SwiftUI

/// The speeds the stepper can reach, and how a listener reads them.
///
/// Its own type because both player screens need the same arithmetic and the same label, and
/// because the rounding below is not optional detail -- it is what keeps the number honest.
enum PlaybackSpeed {
    static let step: Float = 0.05
    static let minimum: Float = 0.25
    static let maximum: Float = 2

    /// Snaps a speed to the nearest step and clamps it to the reachable range.
    ///
    /// Rounded rather than simply added to. Repeatedly adding 0.05 to a Float accumulates error,
    /// and that value is persisted per book and then formatted -- so after enough presses a
    /// listener would be looking at 1.4500001x, and the stored speed would never again land on a
    /// clean stop.
    static func stepped(_ rate: Float) -> Float {
        let snapped = (rate / step).rounded() * step
        return min(max(snapped, minimum), maximum)
    }

    static func slower(_ rate: Float) -> Float { stepped(rate - step) }
    static func faster(_ rate: Float) -> Float { stepped(rate + step) }

    /// 1.0x, 1.05x, 1.1x, 1.15x -- two decimals only when the step lands on one.
    ///
    /// The whole number keeps its single decimal, so the label is 1.0x rather than 1x: this sits in
    /// a fixed-width row and the width should not shift as the number does.
    static func label(_ rate: Float) -> String {
        let hundredths = Int((rate * 100).rounded())
        let value = Float(hundredths) / 100
        return hundredths % 10 == 0
            ? String(format: "%.1f\u{00D7}", value)
            : String(format: "%.2f\u{00D7}", value)
    }
}

/// The minus / 1.05x / plus control shown when Speed is tapped.
///
/// A popover rather than a `Menu`, because a menu dismisses on the first tap and finding the right
/// speed for a narrator means pressing several times while listening. Presented as a popover even
/// on iPhone, where SwiftUI would otherwise adapt it into a sheet that covers the player.
struct SpeedStepper: View {
    @ObservedObject var playback: AudioPlaybackManager

    var body: some View {
        HStack(spacing: 18) {
            Button {
                playback.setRate(PlaybackSpeed.slower(playback.playbackRate))
            } label: {
                Image(systemName: "minus")
                    .font(.title3.weight(.semibold))
                    .frame(width: 44, height: 44)
            }
            .disabled(playback.playbackRate <= PlaybackSpeed.minimum)
            .accessibilityLabel("Slower")

            Text(PlaybackSpeed.label(playback.playbackRate))
                .font(.title3.weight(.semibold))
                .foregroundStyle(ACTheme.accent)
                // Fixed width so the row does not jump as the label moves between one and two
                // decimals while a listener is still pressing.
                .frame(width: 86)
                .monospacedDigit()

            Button {
                playback.setRate(PlaybackSpeed.faster(playback.playbackRate))
            } label: {
                Image(systemName: "plus")
                    .font(.title3.weight(.semibold))
                    .frame(width: 44, height: 44)
            }
            .disabled(playback.playbackRate >= PlaybackSpeed.maximum)
            .accessibilityLabel("Faster")
        }
        .tint(ACTheme.accent)
        .padding(.horizontal, 10)
        .padding(.vertical, 6)
        .presentationCompactAdaptation(.popover)
    }
}
