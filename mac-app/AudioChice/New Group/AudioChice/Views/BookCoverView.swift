import AppKit
import SwiftUI

struct BookCoverView: View {

    let book: Book
    var height: CGFloat = 290

    var body: some View {

        Group {

            if let coverArtData = book.coverArtData,
               let image = NSImage(data: coverArtData) {

                Image(nsImage: image)
                    .resizable()
                    .scaledToFill()

            } else {

                LinearGradient(
                    colors: [
                        Color.green.opacity(0.7),
                        Color.black
                    ],
                    startPoint: .topLeading,
                    endPoint: .bottomTrailing
                )
                .overlay {

                    Image(systemName: "waveform")
                        .font(.system(size: 56))
                        .foregroundStyle(.white.opacity(0.9))
                }
            }
        }
        .frame(height: height)
        .frame(maxWidth: .infinity)
        .clipShape(
            RoundedRectangle(cornerRadius: 16)
        )
        .shadow(
            color: .black.opacity(0.45),
            radius: 12,
            y: 6
        )
    }
}

#Preview {

    BookCoverView(
        book: Book(
            title: "Preview",
            originalFileURL: URL(fileURLWithPath: "/"),
            fileType: "mp3"
        )
    )
}
