import SwiftUI

struct RecentlyAddedView: View {

    let books: [Book]

    let onOpen: (Book) -> Void
    let onPlay: (Book) -> Void
    let onDelete: (Book) -> Void

    var body: some View {

        ZStack {

            Color.black.opacity(0.96)
                .ignoresSafeArea()

            if books.isEmpty {

                VStack(spacing: 20) {

                    Image(systemName: "clock.arrow.circlepath")
                        .font(.system(size: 70))
                        .foregroundStyle(.green)

                    Text("Recently Added")
                        .font(.largeTitle.bold())

                    Text("Newly imported audiobooks will appear here.")
                        .foregroundStyle(.secondary)
                }

            } else {

                ScrollView {

                    VStack(alignment: .leading, spacing: 24) {

                        Text("Recently Added")
                            .font(.system(size: 32, weight: .bold))

                        LazyVGrid(
                            columns: [
                                GridItem(
                                    .adaptive(
                                        minimum: 190,
                                        maximum: 240
                                    ),
                                    spacing: 24
                                )
                            ],
                            alignment: .leading,
                            spacing: 30
                        ) {

                            ForEach(books.reversed()) { book in

                                BookCardView(
                                    book: book,
                                    onOpen: {
                                        onOpen(book)
                                    },
                                    onPlay: {
                                        onPlay(book)
                                    },
                                    onDelete: {
                                        onDelete(book)
                                    }
                                )
                            }
                        }
                    }
                    .padding(32)
                }
            }
        }
    }
}

#Preview {

    RecentlyAddedView(
        books: [],
        onOpen: { _ in },
        onPlay: { _ in },
        onDelete: { _ in }
    )
}
