import SwiftUI

struct LibraryHeaderView: View {

    let bookCount: Int
    let onImport: () -> Void

    var body: some View {

        HStack {

            VStack(alignment: .leading, spacing: 5) {

                Text("Your Library")
                    .font(.system(size: 32, weight: .bold))

                Text(
                    bookCount == 1
                    ? "1 audiobook"
                    : "\(bookCount) audiobooks"
                )
                .foregroundStyle(.secondary)
            }

            Spacer()

            Button(action: onImport) {

                Label(
                    "Import Audiobook",
                    systemImage: "plus"
                )
            }
            .buttonStyle(.borderedProminent)
            .tint(.green)
            .controlSize(.large)
        }
    }
}

#Preview {

    LibraryHeaderView(
        bookCount: 24,
        onImport: {}
    )
}
