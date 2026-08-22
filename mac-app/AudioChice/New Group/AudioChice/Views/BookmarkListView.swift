import SwiftUI

struct BookmarkListView: View {

    let book: Book
    let currentTime: TimeInterval

    let onAdd: (
        _ position: TimeInterval,
        _ title: String,
        _ note: String?
    ) -> Void

    let onSelect: (TimeInterval) -> Void
    let onDelete: (UUID) -> Void

    @Environment(\.dismiss) private var dismiss

    @State private var showingAddBookmark = false
    @State private var bookmarkTitle = ""
    @State private var bookmarkNote = ""

    var body: some View {
        NavigationStack {
            Group {
                if book.bookmarks.isEmpty {
                    VStack(spacing: 18) {
                        Image(systemName: "bookmark")
                            .font(.system(size: 54))
                            .foregroundStyle(.green)

                        Text("No Bookmarks")
                            .font(.title2.bold())

                        Text(
                            "Add a bookmark to save your current listening position."
                        )
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)

                        Button("Add Bookmark") {
                            prepareNewBookmark()
                        }
                        .buttonStyle(.borderedProminent)
                        .tint(.green)
                    }
                    .frame(
                        maxWidth: .infinity,
                        maxHeight: .infinity
                    )
                    .padding(40)

                } else {
                    List {
                        ForEach(book.bookmarks) { bookmark in
                            Button {
                                onSelect(bookmark.position)
                                dismiss()
                            } label: {
                                HStack(spacing: 14) {
                                    Image(systemName: "bookmark.fill")
                                        .foregroundStyle(.green)

                                    VStack(
                                        alignment: .leading,
                                        spacing: 5
                                    ) {
                                        Text(bookmark.title)
                                            .font(.headline)

                                        Text(
                                            formattedTime(
                                                bookmark.position
                                            )
                                        )
                                        .font(.caption.monospacedDigit())
                                        .foregroundStyle(.secondary)

                                        if let note = bookmark.note,
                                           !note.isEmpty {
                                            Text(note)
                                                .font(.subheadline)
                                                .foregroundStyle(.secondary)
                                                .lineLimit(2)
                                        }
                                    }

                                    Spacer()
                                }
                                .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                            .contextMenu {
                                Button(role: .destructive) {
                                    onDelete(bookmark.id)
                                } label: {
                                    Label(
                                        "Delete Bookmark",
                                        systemImage: "trash"
                                    )
                                }
                            }
                        }
                        .onDelete { offsets in
                            let bookmarks = book.bookmarks

                            for offset in offsets {
                                guard bookmarks.indices.contains(offset)
                                else {
                                    continue
                                }

                                onDelete(bookmarks[offset].id)
                            }
                        }
                    }
                }
            }
            .navigationTitle("Bookmarks")
            .toolbar {
                ToolbarItem {
                    Button {
                        prepareNewBookmark()
                    } label: {
                        Label(
                            "Add Bookmark",
                            systemImage: "plus"
                        )
                    }
                }
            }
            .sheet(isPresented: $showingAddBookmark) {
                addBookmarkView
            }
        }
        .frame(minWidth: 460, minHeight: 520)
    }

    private var addBookmarkView: some View {
        NavigationStack {
            Form {
                TextField(
                    "Bookmark title",
                    text: $bookmarkTitle
                )

                TextField(
                    "Optional note",
                    text: $bookmarkNote,
                    axis: .vertical
                )
                .lineLimit(3...6)

                LabeledContent(
                    "Position",
                    value: formattedTime(currentTime)
                )
            }
            .padding()
            .navigationTitle("Add Bookmark")
            .toolbar {
                ToolbarItem(
                    placement: .cancellationAction
                ) {
                    Button("Cancel") {
                        showingAddBookmark = false
                    }
                }

                ToolbarItem(
                    placement: .confirmationAction
                ) {
                    Button("Save") {
                        let cleanedNote =
                            bookmarkNote.trimmingCharacters(
                                in: .whitespacesAndNewlines
                            )

                        onAdd(
                            currentTime,
                            bookmarkTitle,
                            cleanedNote.isEmpty
                                ? nil
                                : cleanedNote
                        )

                        showingAddBookmark = false
                    }
                }
            }
        }
        .frame(minWidth: 420, minHeight: 300)
    }

    private func prepareNewBookmark() {
        bookmarkTitle = defaultBookmarkTitle
        bookmarkNote = ""
        showingAddBookmark = true
    }

    private var defaultBookmarkTitle: String {
        let chapter = book.chapters.last {
            $0.startTime <= currentTime
        }

        return chapter?.title
            ?? "Bookmark at \(formattedTime(currentTime))"
    }

    private func formattedTime(
        _ time: TimeInterval
    ) -> String {
        let totalSeconds = max(0, Int(time))
        let hours = totalSeconds / 3600
        let minutes = (totalSeconds % 3600) / 60
        let seconds = totalSeconds % 60

        if hours > 0 {
            return String(
                format: "%d:%02d:%02d",
                hours,
                minutes,
                seconds
            )
        }

        return String(
            format: "%d:%02d",
            minutes,
            seconds
        )
    }
}

#Preview {
    BookmarkListView(
        book: Book(
            title: "Preview",
            originalFileURL: URL(fileURLWithPath: "/"),
            fileType: "mp3"
        ),
        currentTime: 125,
        onAdd: { _, _, _ in },
        onSelect: { _ in },
        onDelete: { _ in }
    )
}
