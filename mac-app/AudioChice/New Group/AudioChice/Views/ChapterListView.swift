import SwiftUI

struct ChapterListView: View {

    let book: Book
    let currentTime: TimeInterval
    let onSelect: (TimeInterval) -> Void

    @Environment(\.dismiss) private var dismiss

    var body: some View {

        NavigationStack {

            List {

                if book.chapters.isEmpty {

                    Section("Audiobook") {

                        VStack(alignment: .leading, spacing: 6) {

                            Text("Audiobook")
                                .font(.headline)

                            Text("No embedded chapter markers were found in this audiobook.")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                        .padding(.vertical, 6)
                    }

                } else {

                    Section("Chapters") {

                        ForEach(book.chapters) { chapter in

                            Button {

                                onSelect(chapter.startTime)
                                dismiss()

                            } label: {

                                HStack {

                                    VStack(alignment: .leading) {

                                        Text(chapter.title)

                                        Text(
                                            formattedTime(chapter.startTime)
                                        )
                                        .font(.caption)
                                        .foregroundStyle(.secondary)
                                    }

                                    Spacer()

                                    if abs(currentTime - chapter.startTime) < 5 {

                                        Image(systemName: "speaker.wave.2.fill")
                                            .foregroundStyle(.green)
                                    }
                                }
                            }
                            .buttonStyle(.plain)
                        }
                    }
                }
            }
            .navigationTitle("Chapters")
        }
        .frame(minWidth: 420, minHeight: 500)
    }

    private func formattedTime(
        _ seconds: TimeInterval
    ) -> String {

        let total = Int(seconds)

        let hours = total / 3600
        let minutes = (total % 3600) / 60
        let secs = total % 60

        if hours > 0 {

            return String(
                format: "%d:%02d:%02d",
                hours,
                minutes,
                secs
            )
        }

        return String(
            format: "%d:%02d",
            minutes,
            secs
        )
    }
}

#Preview {

    ChapterListView(

        book: Book(
            title: "Preview",
            originalFileURL: URL(fileURLWithPath: "/"),
            fileType: "mp3"
        ),

        currentTime: 0

    ) { _ in

    }
}
