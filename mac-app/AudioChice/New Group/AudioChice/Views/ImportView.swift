import SwiftUI

struct ImportView: View {

    let onImport: () -> Void

    var body: some View {

        ZStack {

            Color.black.opacity(0.96)
                .ignoresSafeArea()

            VStack(spacing: 26) {

                Image(systemName: "arrow.up.doc.fill")
                    .font(.system(size: 74))
                    .foregroundStyle(.green)

                VStack(spacing: 8) {

                    Text("Import Audiobook")
                        .font(.system(size: 34, weight: .bold))

                    Text("Choose an audiobook file you own or have permission to use.")
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                }

                VStack(spacing: 18) {

                    Button(action: onImport) {

                        Label(
                            "Browse Files",
                            systemImage: "folder"
                        )
                        .frame(minWidth: 180)
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(.green)
                    .controlSize(.large)

                    Text("Supported Formats")
                        .font(.caption)
                        .foregroundStyle(.secondary)

                    HStack(spacing: 20) {

                        formatBadge("MP3")
                        formatBadge("M4B")
                        formatBadge("M4A")
                        formatBadge("AAX")
                    }
                }

                Text("""
Audiobook files remain on your device.

AudioChoice only creates metadata, fingerprints, and scan information.
""")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 500)
                .padding(.top, 8)

                Spacer()
            }
            .padding(40)
        }
    }

    private func formatBadge(
        _ title: String
    ) -> some View {

        Text(title)
            .font(.caption.weight(.semibold))
            .foregroundStyle(.green)
            .padding(.horizontal, 12)
            .padding(.vertical, 7)
            .background(
                Color.green.opacity(0.12)
            )
            .clipShape(Capsule())
    }
}

#Preview {

    ImportView(
        onImport: {}
    )
}
