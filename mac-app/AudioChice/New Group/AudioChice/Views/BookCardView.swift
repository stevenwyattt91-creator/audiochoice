import SwiftUI

struct BookCardView: View {

    let book: Book

    let onOpen: () -> Void
    let onPlay: () -> Void
    let onDelete: () -> Void

    var body: some View {

        Button(action: onOpen) {

            VStack(alignment: .leading, spacing: 10) {

                BookCoverView(book: book)

                Text(book.identity?.workTitle ?? book.title)
                    .font(.headline)
                    .foregroundStyle(.primary)
                    .lineLimit(2)

                if let author = book.author,
                   !author.isEmpty {

                    Text(author)
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }

                HStack(spacing: 6) {

                    Text(book.fileType.uppercased())
                        .foregroundStyle(.green)

                    if let duration = book.duration {

                        Text("•")

                        Text(formattedDuration(duration))
                    }
                }
                .font(.caption)
                .foregroundStyle(.secondary)

                if let identity = book.identity {

                    Text(identity.editionType.displayName)
                        .font(.caption)
                        .foregroundStyle(.green)
                        .lineLimit(1)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .contextMenu {

            Button {

                onOpen()

            } label: {

                Label(
                    "Open Book",
                    systemImage: "book.open"
                )
            }

            Button {

                onPlay()

            } label: {

                Label(
                    "Play Audiobook",
                    systemImage: "play.fill"
                )
            }

            Divider()

            Button(role: .destructive) {

                onDelete()

            } label: {

                Label(
                    "Remove from Library",
                    systemImage: "trash"
                )
            }
        }
    }

    private func formattedDuration(
        _ duration: TimeInterval
    ) -> String {

        let totalSeconds = max(0, Int(duration))

        let hours = totalSeconds / 3600
        let minutes = (totalSeconds % 3600) / 60

        if hours > 0 {

            return "\(hours) hr \(minutes) min"
        }

        return "\(minutes) min"
    }
}

#Preview {

    BookCardView(

        book: Book(
            title: "Preview Book",
            originalFileURL: URL(fileURLWithPath: "/"),
            fileType: "mp3"
        ),

        onOpen: {},
        onPlay: {},
        onDelete: {}
    )
}
