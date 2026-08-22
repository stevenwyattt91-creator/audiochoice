import SwiftUI

struct PlaybackSpeedView: View {

    let selected: Double
    let onSelect:(Double)->Void

    private let speeds:[Double] = [
        0.75,
        1,
        1.25,
        1.5,
        1.75,
        2
    ]

    var body: some View {

        VStack(spacing:10){

            Text("Playback Speed")
                .font(.headline)

            HStack{

                ForEach(
                    speeds,
                    id:\.self
                ){ speed in

                    Button{

                        onSelect(speed)

                    }label:{

                        Text(label(speed))
                            .padding(.horizontal,10)
                            .padding(.vertical,6)
                            .background(
                                abs(selected-speed)<0.01
                                ? Color.green
                                : Color.clear
                            )
                            .foregroundStyle(
                                abs(selected-speed)<0.01
                                ? Color.black
                                : Color.primary
                            )
                            .clipShape(Capsule())
                    }
                    .buttonStyle(.plain)
                }
            }
        }
    }

    private func label(
        _ speed:Double
    )->String{

        speed == floor(speed)
        ? "\(Int(speed))x"
        : "\(speed)x"
    }
}
