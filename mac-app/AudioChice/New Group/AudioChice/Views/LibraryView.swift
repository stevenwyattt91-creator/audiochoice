import SwiftUI

struct LibraryView: View {

    let books: [Book]

    let onImport: () -> Void
    let onOpen: (Book) -> Void
    let onPlay: (Book) -> Void
    let onDelete: (Book) -> Void

    var body: some View {

        ZStack {

            Color.black.opacity(0.96)
                .ignoresSafeArea()

            if books.isEmpty {

                EmptyLibraryView(
                    onImport: onImport
                )

            } else {

                ScrollView {

                    VStack(
                        alignment: .leading,
                        spacing: 24
                    ) {

                        LibraryHeaderView(
                            bookCount: books.count,
                            onImport: onImport
                        )

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

                            ForEach(books) { book in

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

    LibraryView(

        books: [],

        onImport: {},

        onOpen: { _ in },

        onPlay: { _ in },

        onDelete: { _ in }
    )
}
