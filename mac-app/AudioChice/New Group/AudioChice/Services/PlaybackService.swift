import AVFAudio
import Combine
import Foundation

enum SleepTimerMode: Equatable {
    case minutes(Int)
    case endOfChapter
}

@MainActor
final class PlaybackService: NSObject, ObservableObject {

    @Published private(set) var currentBookID: UUID?
    @Published private(set) var isPlaying = false
    @Published private(set) var currentTime: TimeInterval = 0
    @Published private(set) var duration: TimeInterval = 0
    @Published private(set) var playbackSpeed: Float = 1.0
    @Published private(set) var playbackError: String?

    // Sleep timer information
    @Published private(set) var sleepTimerMode: SleepTimerMode?
    @Published private(set) var sleepTimerRemaining: TimeInterval?
    @Published private(set) var sleepTimerTargetTime: TimeInterval?

    private var audioPlayer: AVAudioPlayer?
    private var progressTimer: Timer?
    private var sleepTimer: Timer?
    private var sleepTimerEndDate: Date?

    var onProgressUpdate: (
        (
            UUID,
            TimeInterval,
            Double
        ) -> Void
    )?

    var remainingTime: TimeInterval {
        max(0, duration - currentTime)
    }

    var progress: Double {
        guard duration > 0 else {
            return 0
        }

        return min(
            max(currentTime / duration, 0),
            1
        )
    }

    var isSleepTimerActive: Bool {
        sleepTimerMode != nil
    }

    var sleepTimerDescription: String? {
        guard let sleepTimerMode else {
            return nil
        }

        switch sleepTimerMode {
        case .minutes:
            guard let sleepTimerRemaining else {
                return nil
            }

            return formattedTimerTime(
                sleepTimerRemaining
            )

        case .endOfChapter:
            guard let sleepTimerRemaining else {
                return "End of Chapter"
            }

            return "Chapter • \(formattedTimerTime(sleepTimerRemaining))"
        }
    }

    // MARK: - Loading

    func load(book: Book) {
        stopProgressTimer()
        cancelSleepTimer()

        playbackError = nil

        let url = book.convertedFileURL
            ?? book.originalFileURL

        guard book.fileType.lowercased() != "aax"
        else {
            playbackError =
                "This AAX audiobook must be converted before playback."

            audioPlayer = nil
            currentBookID = nil
            isPlaying = false
            currentTime = 0
            duration = 0
            return
        }

        do {
            let player = try AVAudioPlayer(
                contentsOf: url
            )

            player.delegate = self
            player.enableRate = true
            player.rate = Float(book.playbackSpeed)
            player.prepareToPlay()

            audioPlayer = player
            currentBookID = book.id
            duration = player.duration

            let savedPosition = min(
                max(book.currentPosition, 0),
                player.duration
            )

            player.currentTime = savedPosition
            currentTime = savedPosition
            playbackSpeed = player.rate
            isPlaying = false

        } catch {
            audioPlayer = nil
            currentBookID = nil
            currentTime = 0
            duration = 0
            isPlaying = false

            playbackError =
                "AudioChoice could not play this audiobook: \(error.localizedDescription)"
        }
    }

    // MARK: - Playback

    func togglePlayPause() {
        if isPlaying {
            pause()
        } else {
            play()
        }
    }

    func play() {
        guard let audioPlayer else {
            return
        }

        guard audioPlayer.play() else {
            playbackError =
                "AudioChoice could not begin playback."
            return
        }

        isPlaying = true
        startProgressTimer()
    }

    func pause() {
        guard let audioPlayer else {
            return
        }

        audioPlayer.pause()
        currentTime = audioPlayer.currentTime

        sendProgressUpdate()

        isPlaying = false
        stopProgressTimer()
    }

    func stop() {
        guard let audioPlayer else {
            return
        }

        audioPlayer.stop()
        audioPlayer.currentTime = 0

        currentTime = 0
        isPlaying = false

        stopProgressTimer()
        cancelSleepTimer()

        sendProgressUpdate()
    }

    // MARK: - Seeking

    func seek(to time: TimeInterval) {
        guard let audioPlayer else {
            return
        }

        let safeTime = min(
            max(time, 0),
            audioPlayer.duration
        )

        audioPlayer.currentTime = safeTime
        currentTime = safeTime

        sendProgressUpdate()
        updateEndOfChapterTimer()
    }

    func seek(toProgress progress: Double) {
        guard duration > 0 else {
            return
        }

        let safeProgress = min(
            max(progress, 0),
            1
        )

        seek(
            to: safeProgress * duration
        )
    }

    func skipForward(
        seconds: TimeInterval
    ) {
        seek(
            to: currentTime + seconds
        )
    }

    func skipBackward(
        seconds: TimeInterval
    ) {
        seek(
            to: currentTime - seconds
        )
    }

    // MARK: - Playback Speed

    func setPlaybackSpeed(
        _ speed: Float
    ) {
        let safeSpeed = min(
            max(speed, 0.5),
            2.0
        )

        playbackSpeed = safeSpeed
        audioPlayer?.rate = safeSpeed

        sendProgressUpdate()
    }

    // MARK: - Sleep Timer

    func startSleepTimer(
        minutes: Int
    ) {
        guard minutes > 0 else {
            cancelSleepTimer()
            return
        }

        cancelSleepTimer()

        let interval =
            TimeInterval(minutes * 60)

        sleepTimerMode = .minutes(minutes)
        sleepTimerRemaining = interval
        sleepTimerTargetTime = nil
        sleepTimerEndDate = Date()
            .addingTimeInterval(interval)

        sleepTimer = Timer.scheduledTimer(
            withTimeInterval: 1,
            repeats: true
        ) { [weak self] _ in
            Task { @MainActor in
                self?.updateMinuteSleepTimer()
            }
        }
    }

    func startEndOfChapterSleepTimer(
        chapterEndTime: TimeInterval
    ) {
        let safeTarget = min(
            max(chapterEndTime, currentTime),
            duration
        )

        guard safeTarget > currentTime else {
            pause()
            cancelSleepTimer()
            return
        }

        cancelSleepTimer()

        sleepTimerMode = .endOfChapter
        sleepTimerTargetTime = safeTarget
        sleepTimerRemaining =
            safeTarget - currentTime
    }

    func cancelSleepTimer() {
        sleepTimer?.invalidate()
        sleepTimer = nil

        sleepTimerMode = nil
        sleepTimerRemaining = nil
        sleepTimerTargetTime = nil
        sleepTimerEndDate = nil
    }

    private func updateMinuteSleepTimer() {
        guard case .minutes = sleepTimerMode,
              let sleepTimerEndDate
        else {
            return
        }

        let remaining =
            sleepTimerEndDate.timeIntervalSinceNow

        if remaining <= 0 {
            expireSleepTimer()
        } else {
            sleepTimerRemaining = remaining
        }
    }

    private func updateEndOfChapterTimer() {
        guard sleepTimerMode == .endOfChapter,
              let targetTime = sleepTimerTargetTime
        else {
            return
        }

        let remaining =
            targetTime - currentTime

        if remaining <= 0 {
            expireSleepTimer()
        } else {
            sleepTimerRemaining = remaining
        }
    }

    private func expireSleepTimer() {
        pause()
        cancelSleepTimer()
    }

    // MARK: - Progress Timer

    private func startProgressTimer() {
        stopProgressTimer()

        progressTimer = Timer.scheduledTimer(
            withTimeInterval: 0.25,
            repeats: true
        ) { [weak self] _ in
            Task { @MainActor in
                guard let self,
                      let audioPlayer = self.audioPlayer
                else {
                    return
                }

                self.currentTime =
                    audioPlayer.currentTime

                self.duration =
                    audioPlayer.duration

                self.sendProgressUpdate()
                self.updateEndOfChapterTimer()

                if !audioPlayer.isPlaying {
                    self.isPlaying = false
                    self.stopProgressTimer()
                }
            }
        }
    }

    private func stopProgressTimer() {
        progressTimer?.invalidate()
        progressTimer = nil
    }

    private func sendProgressUpdate() {
        guard let bookID = currentBookID else {
            return
        }

        onProgressUpdate?(
            bookID,
            currentTime,
            Double(playbackSpeed)
        )
    }

    private func formattedTimerTime(
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

    deinit {
        progressTimer?.invalidate()
        sleepTimer?.invalidate()
    }
}

// MARK: - AVAudioPlayerDelegate

extension PlaybackService: AVAudioPlayerDelegate {

    nonisolated func audioPlayerDidFinishPlaying(
        _ player: AVAudioPlayer,
        successfully flag: Bool
    ) {
        Task { @MainActor in
            currentTime = player.duration
            isPlaying = false

            sendProgressUpdate()
            stopProgressTimer()
            cancelSleepTimer()
        }
    }

    nonisolated func audioPlayerDecodeErrorDidOccur(
        _ player: AVAudioPlayer,
        error: Error?
    ) {
        Task { @MainActor in
            isPlaying = false

            stopProgressTimer()
            cancelSleepTimer()

            playbackError =
                error?.localizedDescription
                ?? "An audio decoding error occurred."
        }
    }
}
