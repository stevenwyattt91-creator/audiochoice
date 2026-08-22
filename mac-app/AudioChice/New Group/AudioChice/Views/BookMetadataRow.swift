import SwiftUI

struct BookMetadataRow: View {

    let title: String
    let value: String

    var body: some View {

        HStack {

            Text(title)
                .foregroundStyle(.secondary)

            Spacer()

            Text(value)
                .fontWeight(.medium)
                .multilineTextAlignment(.trailing)
                .textSelection(.enabled)
        }
    }
}

#Preview {

    BookMetadataRow(
        title: "Duration",
        value: "41 hr 23 min"
    )
}
