import SwiftUI

struct BookDetailView: View {

    let book: Book

    let onBack: () -> Void
    let onPlay: () -> Void
    let onFilters: () -> Void

    var body: some View {

        ZStack {

            Color.black.opacity(0.96)
                .ignoresSafeArea()

            ScrollView {

                VStack(spacing: 24) {

                    HStack {

                        Button(action: onBack) {

                            Label(
                                "Back to Library",
                                systemImage: "chevron.left"
                            )
                        }
                        .buttonStyle(.plain)
                        .foregroundStyle(.green)

                        Spacer()
                    }

                    BookCoverView(
                        book: book,
                        height: 380
                    )
                    .frame(maxWidth: 320)

                    VStack(spacing: 8) {

                        Text(book.identity?.workTitle ?? book.title)
                            .font(.system(size: 30, weight: .bold))
                            .multilineTextAlignment(.center)
                            .lineLimit(3)
                            .minimumScaleFactor(0.7)

                        if let author = book.author {

                            Text(author)
                                .font(.title3)
                                .foregroundStyle(.secondary)
                        }

                        if let identity = book.identity {

                            VStack(spacing: 5) {

                                if let series = identity.seriesTitle {

                                    if let number = identity.seriesNumber {

                                        Text("\(series) • Book \(number)")

                                    } else {

                                        Text(series)
                                    }
                                }

                                Text(identity.editionType.displayName)
                                    .foregroundStyle(.green)

                                if let part = identity.partNumber,
                                   let total = identity.totalParts {

                                    Text("Part \(part) of \(total)")
                                }
                            }
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                        }

                        HStack(spacing: 10) {

                            Text(book.fileType.uppercased())
                                .font(.caption.weight(.semibold))
                                .foregroundStyle(.green)

                            if let duration = book.duration {

                                Text("•")

                                Text(formattedDuration(duration))
                                    .foregroundStyle(.secondary)
                            }
                        }
                    }

                    HStack(spacing: 14) {

                        Button(action: onPlay) {

                            Label(
                                "Play",
                                systemImage: "play.fill"
                            )
                            .frame(minWidth: 120)
                        }
                        .buttonStyle(.borderedProminent)
                        .tint(.green)
                        .controlSize(.large)

                        Button(action: onFilters) {

                            Label(
                                "Filters",
                                systemImage: "line.3.horizontal.decrease.circle"
                            )
                            .frame(minWidth: 120)
                        }
                        .buttonStyle(.bordered)
                        .controlSize(.large)
                    }

                    BookInformationView(
                        book: book
                    )
                    .frame(maxWidth: 560)
                }
                .frame(maxWidth: .infinity)
                .padding(40)
            }
        }
    }

    private func formattedDuration(
        _ duration: TimeInterval
    ) -> String {

        let total = Int(duration)

        let hours = total / 3600
        let minutes = (total % 3600) / 60

        if hours > 0 {

            return "\(hours) hr \(minutes) min"
        }

        return "\(minutes) min"
    }
}

#Preview {

    BookDetailView(

        book: Book(
            title: "Preview",
            originalFileURL: URL(fileURLWithPath: "/"),
            fileType: "mp3"
        ),

        onBack: {},

        onPlay: {},

        onFilters: {}
    )
}
