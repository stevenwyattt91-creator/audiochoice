import SwiftUI

struct PlaybackControlsView: View {

    let playing: Bool

    let back60: () -> Void
    let back10: () -> Void
    let playPause: () -> Void
    let forward10: () -> Void
    let forward60: () -> Void

    var body: some View {

        HStack(spacing:32) {

            button(
                "gobackward.60",
                action: back60
            )

            button(
                "gobackward.10",
                action: back10
            )

            Button(action: playPause) {

                Circle()
                    .fill(.green)
                    .frame(width:78,height:78)
                    .overlay {

                        Image(
                            systemName: playing
                            ? "pause.fill"
                            : "play.fill"
                        )
                        .font(.system(size:30))
                        .foregroundStyle(.black)
                    }
            }
            .buttonStyle(.plain)

            button(
                "goforward.10",
                action: forward10
            )

            button(
                "goforward.60",
                action: forward60
            )
        }
    }

    private func button(
        _ icon:String,
        action:@escaping()->Void
    )->some View{

        Button(action: action){

            Image(systemName: icon)
                .font(.title2)
        }
        .buttonStyle(.plain)
    }
}

#Preview {

    PlaybackControlsView(
        playing: true,
        back60: {},
        back10: {},
        playPause: {},
        forward10: {},
        forward60: {}
    )
}
