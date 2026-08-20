import AVFoundation
import Foundation
import MediaPlayer
import UIKit

@MainActor
final class AudioPlaybackManager: ObservableObject {
    static let shared = AudioPlaybackManager()

    @Published private(set) var currentBookID: UUID?
    @Published private(set) var currentRecord: LibraryBookRecord?
    @Published private(set) var isPlaying = false
    @Published private(set) var position: Double = 0
    @Published private(set) var duration: Double = 0
    @Published private(set) var currentChapterTitle = ""
    @Published var playbackRate: Float = 1
    @Published private(set) var activeFilterEvent: ScanEvent?
    @Published private(set) var skippedEventCount = 0

    private var player: AVPlayer?
    private var timeObserver: Any?
    private var record: LibraryBookRecord?
    private var lastHandledEventID: UUID?

    private init() {
        configureRemoteCommands()
    }

    static func savedPosition(for bookID: UUID) -> Double {
        UserDefaults.standard.double(forKey: "playbackPosition.\(bookID.uuidString)")
    }

    func load(_ record: LibraryBookRecord) {
        guard currentBookID != record.id,
              let fileName = record.localFileName else { return }
        removeTimeObserver()
        self.record = record
        self.currentRecord = record
        UserDefaults.standard.set(record.id.uuidString, forKey: "lastPlayedBookID")
        self.lastHandledEventID = nil
        self.activeFilterEvent = nil
        self.skippedEventCount = 0
        currentBookID = record.id
        let item = AVPlayerItem(url: AudiobookImportService.audioURL(fileName: fileName))
        player = AVPlayer(playerItem: item)
        position = UserDefaults.standard.double(forKey: positionKey(record.id))
        player?.seek(to: CMTime(seconds: position, preferredTimescale: 600))
        addTimeObserver()
        Task {
            duration = (try? await item.asset.load(.duration).seconds) ?? 0
            updateChapter()
            updateNowPlaying()
        }
    }

    func togglePlayback() {
        guard let player else { return }
        if isPlaying {
            player.pause()
            isPlaying = false
        } else {
            do {
                try AVAudioSession.sharedInstance().setCategory(.playback, mode: .spokenAudio)
                try AVAudioSession.sharedInstance().setActive(true)
                player.playImmediately(atRate: playbackRate)
                isPlaying = true
            } catch {
                return
            }
        }
        updateNowPlaying()
    }

    func seek(to seconds: Double) {
        let target = min(max(seconds, 0), duration)
        player?.seek(to: CMTime(seconds: target, preferredTimescale: 600))
        position = target
        persistPosition()
        updateChapter()
        updateNowPlaying()
    }

    func skip(by seconds: Double) { seek(to: position + seconds) }

    func setRate(_ rate: Float) {
        playbackRate = rate
        if isPlaying { player?.rate = rate }
        updateNowPlaying()
    }

    func nextChapter() {
        guard let chapters = record?.chapterMarkers, !chapters.isEmpty else { skip(by: 30); return }
        if let next = chapters.first(where: { $0.startTime > position + 1 }) { seek(to: next.startTime) }
    }

    func previousChapter() {
        guard let chapters = record?.chapterMarkers, !chapters.isEmpty else { skip(by: -15); return }
        let previous = chapters.last(where: { $0.startTime < position - 5 }) ?? chapters.first
        if let previous { seek(to: previous.startTime) }
    }

    private func addTimeObserver() {
        timeObserver = player?.addPeriodicTimeObserver(
            forInterval: CMTime(seconds: 0.5, preferredTimescale: 600),
            queue: .main
        ) { [weak self] time in
            Task { @MainActor in
                guard let self else { return }
                self.position = max(time.seconds, 0)
                self.persistPosition()
                self.updateChapter()
                self.applyContentFilter()
                self.updateNowPlaying()
            }
        }
    }

    private func removeTimeObserver() {
        if let timeObserver { player?.removeTimeObserver(timeObserver) }
        timeObserver = nil
        player?.pause()
        isPlaying = false
    }

    private func persistPosition() {
        guard let id = currentBookID else { return }
        UserDefaults.standard.set(position, forKey: positionKey(id))
    }

    func syncCurrentProgressToAccount() async {
        guard let record = currentRecord, let accountID = record.accountLibraryID else { return }
        try? await CloudScanClient.configured().saveProgress(bookID: accountID, position: position)
    }

    private func positionKey(_ id: UUID) -> String { "playbackPosition.\(id.uuidString)" }

    private func updateChapter() {
        currentChapterTitle = record?.chapterMarkers?
            .last(where: { $0.startTime <= position })?.title ?? "Audiobook"
    }

    private func applyContentFilter() {
        guard let events = record?.scanResult?.events else {
            player?.isMuted = false
            activeFilterEvent = nil
            return
        }
        let matching = events.first { event in
            guard event.startTime <= position, position < event.endTime,
                  let category = IOSContentTaxonomy.category(for: event) else { return false }
            return FilterPreferences.isEnabled(category)
        }
        activeFilterEvent = matching
        guard let matching else {
            player?.isMuted = false
            lastHandledEventID = nil
            return
        }
        switch FilterPreferences.behavior {
        case .mute:
            player?.isMuted = true
        case .skip:
            player?.isMuted = false
            guard lastHandledEventID != matching.id else { return }
            lastHandledEventID = matching.id
            skippedEventCount += 1
            seek(to: matching.endTime + 0.2)
        }
    }

    private func configureRemoteCommands() {
        let commands = MPRemoteCommandCenter.shared()
        commands.playCommand.addTarget { [weak self] _ in
            Task { @MainActor in if self?.isPlaying == false { self?.togglePlayback() } }
            return .success
        }
        commands.pauseCommand.addTarget { [weak self] _ in
            Task { @MainActor in if self?.isPlaying == true { self?.togglePlayback() } }
            return .success
        }
        commands.skipForwardCommand.preferredIntervals = [30]
        commands.skipForwardCommand.addTarget { [weak self] _ in
            Task { @MainActor in self?.skip(by: 30) }
            return .success
        }
        commands.skipBackwardCommand.preferredIntervals = [15]
        commands.skipBackwardCommand.addTarget { [weak self] _ in
            Task { @MainActor in self?.skip(by: -15) }
            return .success
        }
        commands.changePlaybackPositionCommand.addTarget { [weak self] event in
            guard let event = event as? MPChangePlaybackPositionCommandEvent else { return .commandFailed }
            Task { @MainActor in self?.seek(to: event.positionTime) }
            return .success
        }
    }

    private func updateNowPlaying() {
        guard let record else { return }
        var info: [String: Any] = [
            MPMediaItemPropertyTitle: record.book.title,
            MPMediaItemPropertyArtist: record.book.author,
            MPMediaItemPropertyPlaybackDuration: duration,
            MPNowPlayingInfoPropertyElapsedPlaybackTime: position,
            MPNowPlayingInfoPropertyPlaybackRate: isPlaying ? playbackRate : 0
        ]
        if let artworkName = record.artworkFileName,
           let image = UIImage(contentsOfFile: AudiobookImportService.artworkURL(fileName: artworkName).path) {
            info[MPMediaItemPropertyArtwork] = MPMediaItemArtwork(boundsSize: image.size) { _ in image }
        }
        MPNowPlayingInfoCenter.default().nowPlayingInfo = info
    }
}
