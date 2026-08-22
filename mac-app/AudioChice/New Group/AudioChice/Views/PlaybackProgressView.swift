import SwiftUI

struct PlaybackProgressView: View {

    @Binding var sliderPosition: Double

    let currentTime: TimeInterval
    let duration: TimeInterval

    let onSeek: (Double) -> Void

    @State private var dragging = false

    var body: some View {

        VStack(spacing: 8) {

            Slider(
                value: $sliderPosition,
                in: 0...max(duration,1),
                onEditingChanged: { editing in

                    dragging = editing

                    if !editing {

                        onSeek(sliderPosition)
                    }
                }
            )
            .tint(.green)

            HStack {

                Text(
                    formatted(currentTime)
                )

                Spacer()

                Text(
                    "-\(formatted(max(duration-currentTime,0)))"
                )
            }
            .font(.caption.monospacedDigit())
            .foregroundStyle(.secondary)
        }
    }

    private func formatted(
        _ time: TimeInterval
    ) -> String {

        let total = Int(time)

        let h = total / 3600
        let m = (total % 3600) / 60
        let s = total % 60

        if h > 0 {

            return String(
                format:"%d:%02d:%02d",
                h,m,s
            )
        }

        return String(
            format:"%d:%02d",
            m,s
        )
    }
}
