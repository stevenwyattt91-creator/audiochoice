import SwiftUI

struct SleepTimerView: View {

    @ObservedObject var playback: PlaybackService
    let book: Book

    @Environment(\.dismiss) private var dismiss

    private let minuteOptions = [
        5,
        10,
        15,
        30,
        45,
        60,
        90,
        120
    ]

    var body: some View {
        NavigationStack {
            List {
                if playback.isSleepTimerActive {
                    Section("Active Timer") {
                        HStack {
                            Label(
                                activeTimerTitle,
                                systemImage: "moon.zzz.fill"
                            )
                            .foregroundStyle(.green)

                            Spacer()

                            if let description =
                                playback.sleepTimerDescription {
                                Text(description)
                                    .font(.headline.monospacedDigit())
                            }
                        }

                        Button(
                            "Cancel Sleep Timer",
                            role: .destructive
                        ) {
                            playback.cancelSleepTimer()
                            dismiss()
                        }
                    }
                }

                Section("Stop Playback After") {
                    ForEach(
                        minuteOptions,
                        id: \.self
                    ) { minutes in
                        Button {
                            playback.startSleepTimer(
                                minutes: minutes
                            )

                            dismiss()
                        } label: {
                            HStack {
                                Text(
                                    minutesLabel(minutes)
                                )

                                Spacer()

                                if playback.sleepTimerMode ==
                                    .minutes(minutes) {
                                    Image(
                                        systemName: "checkmark"
                                    )
                                    .foregroundStyle(.green)
                                }
                            }
                        }
                        .buttonStyle(.plain)
                    }
                }

                Section("Chapter") {
                    Button {
                        guard let chapterEndTime else {
                            return
                        }

                        playback
                            .startEndOfChapterSleepTimer(
                                chapterEndTime:
                                    chapterEndTime
                            )

                        dismiss()
                    } label: {
                        HStack {
                            VStack(
                                alignment: .leading,
                                spacing: 4
                            ) {
                                Text("End of Current Chapter")

                                if let chapterEndTime {
                                    Text(
                                        "Stops in \(formattedTime(chapterEndTime - playback.currentTime))"
                                    )
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                                } else {
                                    Text(
                                        "No embedded chapter markers available."
                                    )
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                                }
                            }

                            Spacer()

                            if playback.sleepTimerMode ==
                                .endOfChapter {
                                Image(
                                    systemName: "checkmark"
                                )
                                .foregroundStyle(.green)
                            }
                        }
                    }
                    .buttonStyle(.plain)
                    .disabled(chapterEndTime == nil)
                }
            }
            .navigationTitle("Sleep Timer")
            .toolbar {
                ToolbarItem(
                    placement: .cancellationAction
                ) {
                    Button("Close") {
                        dismiss()
                    }
                }
            }
        }
        .frame(minWidth: 420, minHeight: 520)
    }

    private var activeTimerTitle: String {
        switch playback.sleepTimerMode {
        case .minutes:
            return "Sleep Timer"

        case .endOfChapter:
            return "End of Chapter"

        case .none:
            return "Sleep Timer"
        }
    }

    private var chapterEndTime: TimeInterval? {
        guard !book.chapters.isEmpty else {
            return nil
        }

        let currentIndex = book.chapters.lastIndex {
            $0.startTime <= playback.currentTime
        }

        guard let currentIndex else {
            return book.chapters.first?.startTime
        }

        let nextIndex =
            book.chapters.index(
                after: currentIndex
            )

        if book.chapters.indices.contains(nextIndex) {
            return book.chapters[nextIndex].startTime
        }

        return book.duration
    }

    private func minutesLabel(
        _ minutes: Int
    ) -> String {
        if minutes < 60 {
            return "\(minutes) Minutes"
        }

        if minutes == 60 {
            return "1 Hour"
        }

        if minutes % 60 == 0 {
            return "\(minutes / 60) Hours"
        }

        let hours = minutes / 60
        let remainingMinutes = minutes % 60

        return "\(hours) hr \(remainingMinutes) min"
    }

    private func formattedTime(
        _ time: TimeInterval
    ) -> String {
        let totalSeconds =
            max(0, Int(time.rounded(.up)))

        let hours =
            totalSeconds / 3600

        let minutes =
            (totalSeconds % 3600) / 60

        let seconds =
            totalSeconds % 60

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
    SleepTimerView(
        playback: PlaybackService(),
        book: Book(
            title: "Preview",
            originalFileURL: URL(
                fileURLWithPath: "/"
            ),
            fileType: "mp3"
        )
    )
}
