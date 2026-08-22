import SwiftUI

struct BookInformationView: View {

    let book: Book

    var body: some View {

        GroupBox {

            VStack(
                alignment: .leading,
                spacing: 12
            ) {

                BookMetadataRow(
                    title: "Original Format",
                    value: book.fileType.uppercased()
                )

                BookMetadataRow(
                    title: "Conversion",
                    value: readableStatus(
                        book.conversionStatus.rawValue
                    )
                )

                BookMetadataRow(
                    title: "Scan",
                    value: readableStatus(
                        book.scanStatus.rawValue
                    )
                )

                if let duration = book.duration {

                    BookMetadataRow(
                        title: "Duration",
                        value: formattedDuration(duration)
                    )
                }

                if let identity = book.identity {

                    Divider()

                    BookIdentityView(
                        identity: identity
                    )
                }

                if let fingerprint = book.fingerprint {

                    Divider()

                    BookMetadataRow(
                        title: "Fingerprint",
                        value: shortenedFingerprint(
                            fingerprint.sha256
                        )
                    )

                    BookMetadataRow(
                        title: "File Size",
                        value: formattedFileSize(
                            fingerprint.fileSize
                        )
                    )

                    BookMetadataRow(
                        title: "Fingerprint Version",
                        value: "Version \(fingerprint.version)"
                    )
                }
            }
            .padding(4)

        } label: {

            Label(
                "Audiobook Information",
                systemImage: "info.circle"
            )
        }
    }

    private func formattedDuration(
        _ duration: TimeInterval
    ) -> String {

        let seconds = Int(duration)

        let hours = seconds / 3600
        let minutes = (seconds % 3600) / 60

        if hours > 0 {

            return "\(hours) hr \(minutes) min"
        }

        return "\(minutes) min"
    }

    private func shortenedFingerprint(
        _ fingerprint: String
    ) -> String {

        guard fingerprint.count > 16 else {
            return fingerprint
        }

        return "\(fingerprint.prefix(8))…\(fingerprint.suffix(8))"
    }

    private func formattedFileSize(
        _ bytes: Int64
    ) -> String {

        let formatter = ByteCountFormatter()

        formatter.allowedUnits = [.useMB, .useGB]
        formatter.countStyle = .file

        return formatter.string(
            fromByteCount: bytes
        )
    }

    private func readableStatus(
        _ value: String
    ) -> String {

        switch value {

        case "notNeeded":
            return "Not Needed"

        case "notScanned":
            return "Not Scanned"

        case "waiting":
            return "Waiting"

        case "converting":
            return "Converting"

        case "completed":
            return "Completed"

        case "failed":
            return "Failed"

        case "scanning":
            return "Scanning"

        default:
            return value
        }
    }
}

#Preview {

    BookInformationView(

        book: Book(
            title: "Preview",
            originalFileURL: URL(fileURLWithPath: "/"),
            fileType: "mp3"
        )
    )
}
