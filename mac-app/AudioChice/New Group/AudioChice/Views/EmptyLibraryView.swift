import SwiftUI

struct EmptyLibraryView: View {

    let onImport: () -> Void

    var body: some View {

        VStack(spacing: 22) {

            Image(systemName: "books.vertical.fill")
                .font(.system(size: 76))
                .foregroundStyle(.green)

            Text("Your Library")
                .font(.system(size: 34, weight: .bold))

            Text("Your imported audiobooks will appear here.")
                .font(.title3)
                .foregroundStyle(.secondary)

            Button(action: onImport) {

                Label(
                    "Import Audiobook",
                    systemImage: "square.and.arrow.down"
                )
                .padding(.horizontal, 8)

            }
            .buttonStyle(.borderedProminent)
            .tint(.green)
            .controlSize(.large)

            Text("Import an MP3, M4B, M4A or AAX audiobook.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .padding(40)
    }
}

#Preview {

    EmptyLibraryView(
        onImport: {}
    )
}
