import SwiftUI

struct BookIdentityView: View {

    let identity: BookIdentity

    var body: some View {

        VStack(alignment: .leading, spacing: 12) {

            BookMetadataRow(
                title: "Edition",
                value: identity.editionType.displayName
            )

            if let seriesTitle = identity.seriesTitle {

                if let number = identity.seriesNumber {

                    BookMetadataRow(
                        title: "Series",
                        value: "\(seriesTitle), Book \(number)"
                    )

                } else {

                    BookMetadataRow(
                        title: "Series",
                        value: seriesTitle
                    )
                }
            }

            if let part = identity.partNumber,
               let total = identity.totalParts {

                BookMetadataRow(
                    title: "Part",
                    value: "\(part) of \(total)"
                )
            }

            BookMetadataRow(
                title: "Confidence",
                value: "\(Int(identity.confidence * 100))%"
            )
        }
    }
}
