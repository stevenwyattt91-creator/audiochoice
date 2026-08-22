import SwiftUI

struct PlayerHeaderView: View {

    let onBack: () -> Void
    let onBookmark: () -> Void

    var body: some View {

        HStack {

            Button(action: onBack) {

                Label(
                    "Library",
                    systemImage: "chevron.left"
                )
            }
            .buttonStyle(.plain)
            .foregroundStyle(.green)

            Spacer()

            VStack(spacing: 2) {

                Text("Now Playing")
                    .font(.headline)

                Text("AudioChoice")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Spacer()

            Button(action: onBookmark) {

                Image(systemName: "bookmark")
                    .font(.title3)
            }
            .buttonStyle(.plain)
            .foregroundStyle(.green)
        }
    }
}

#Preview {

    PlayerHeaderView(
        onBack: {},
        onBookmark: {}
    )
}
