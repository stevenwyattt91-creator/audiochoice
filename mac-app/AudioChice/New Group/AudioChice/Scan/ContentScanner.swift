import Foundation

final class ContentScanner {

    private let speechRecognitionService =
        SpeechRecognitionService()

    private let transcriptStorage =
        TranscriptStorageService()

    func scan(
        book: Book
    ) async -> ScanResult {

        do {

            let transcript: Transcript

            if let cachedTranscript =
                try transcriptStorage.load(for: book.id) {

                transcript = cachedTranscript

                print(
                    "Loaded cached transcript (\(transcript.segments.count) segments)."
                )

            } else {

                let segments =
                    try await speechRecognitionService
                        .transcribe(book: book)

                transcript = Transcript(
                    segments: segments
                )

                try transcriptStorage.save(
                    transcript,
                    for: book.id
                )

                print(
                    "Created new transcript (\(transcript.segments.count) segments)."
                )
            }

            // AI analysis comes next.
            return ScanResult(events: [])

        } catch {

            print(
                "Content scan failed: \(error.localizedDescription)"
            )

            return ScanResult(events: [])
        }
    }
}
