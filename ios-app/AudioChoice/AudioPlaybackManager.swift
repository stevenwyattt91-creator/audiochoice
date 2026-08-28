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
    @Published private(set) var playbackError: String?
    /// Whether this book's filters can currently be enforced. Surfaced in the player
    /// because filtering silently doing nothing is worse than saying so: the listener
    /// would otherwise assume their filters were active.
    @Published private(set) var filterAvailability: FilterAvailability = .unavailable

    private var player: AVPlayer?
    private var timeObserver: Any?
    private var record: LibraryBookRecord?
    private var lastHandledEventID: UUID?
    private var itemStatusObservation: NSKeyValueObservation?

    private init() {
        configureRemoteCommands()
        configureAudioSession()
        observeAudioSession()
    }

    /// Configured once at startup rather than on the first play.
    ///
    /// Doing it inside togglePlayback meant the very first tap both configured the
    /// session and started playback, and a throw there returned silently — so a
    /// session failure looked like a dead button.
    private func configureAudioSession() {
        do {
            try AVAudioSession.sharedInstance().setCategory(.playback, mode: .spokenAudio)
        } catch {
            playbackError = "This device would not allow background audio playback."
        }
    }

    /// Audiobooks are listened to for hours, so interruptions are normal rather than
    /// exceptional: calls, alarms, and headphones being unplugged. Without these the
    /// player was left believing it was still playing after the system paused it.
    private func observeAudioSession() {
        let center = NotificationCenter.default
        center.addObserver(
            forName: AVAudioSession.interruptionNotification,
            object: AVAudioSession.sharedInstance(),
            queue: .main
        ) { [weak self] notification in
            Task { @MainActor in self?.handleInterruption(notification) }
        }
        center.addObserver(
            forName: AVAudioSession.routeChangeNotification,
            object: AVAudioSession.sharedInstance(),
            queue: .main
        ) { [weak self] notification in
            Task { @MainActor in self?.handleRouteChange(notification) }
        }
        // Progress was otherwise only pushed on pause or when switching books, so
        // leaving the app mid-chapter left the server behind and another device would
        // resume somewhere the listener had already been.
        center.addObserver(
            forName: UIApplication.didEnterBackgroundNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                self.persistPosition()
                await self.syncCurrentProgressToAccount()
            }
        }
    }

    private func handleInterruption(_ notification: Notification) {
        guard let raw = notification.userInfo?[AVAudioSessionInterruptionTypeKey] as? UInt,
              let type = AVAudioSession.InterruptionType(rawValue: raw) else { return }
        switch type {
        case .began:
            player?.pause()
            isPlaying = false
            persistPosition()
            updateNowPlaying()
            Task { await syncCurrentProgressToAccount() }
        case .ended:
            // Only resume when the system says we may. Resuming otherwise fails
            // silently and leaves the UI claiming to play.
            let options = (notification.userInfo?[AVAudioSessionInterruptionOptionKey] as? UInt)
                .map(AVAudioSession.InterruptionOptions.init(rawValue:)) ?? []
            guard options.contains(.shouldResume) else { return }
            try? AVAudioSession.sharedInstance().setActive(true)
            player?.playImmediately(atRate: playbackRate)
            isPlaying = true
            updateNowPlaying()
        @unknown default:
            break
        }
    }

    private func handleRouteChange(_ notification: Notification) {
        guard let raw = notification.userInfo?[AVAudioSessionRouteChangeReasonKey] as? UInt,
              let reason = AVAudioSession.RouteChangeReason(rawValue: raw),
              reason == .oldDeviceUnavailable else { return }
        // Headphones pulled out. Continuing aloud from a phone speaker is exactly what
        // a listener using a filtering audiobook app does not want.
        player?.pause()
        isPlaying = false
        persistPosition()
        updateNowPlaying()
    }

    static func savedPosition(for bookID: UUID) -> Double {
        UserDefaults.standard.double(forKey: "playbackPosition.\(bookID.uuidString)")
    }

    static func applyRemotePosition(_ position: Double, updatedAt: Date, for bookID: UUID) {
        let defaults = UserDefaults.standard
        let timestampKey = "playbackPositionUpdatedAt.\(bookID.uuidString)"
        if defaults.object(forKey: timestampKey) == nil,
           defaults.double(forKey: "playbackPosition.\(bookID.uuidString)") > 0 {
            // Preserve positions saved by builds released before timestamps were
            // recorded; the next playback update will establish ordering.
            defaults.set(Date(), forKey: timestampKey)
            return
        }
        let localUpdate = defaults.object(forKey: timestampKey) as? Date ?? .distantPast
        guard updatedAt > localUpdate else { return }
        defaults.set(max(position, 0), forKey: "playbackPosition.\(bookID.uuidString)")
        defaults.set(updatedAt, forKey: timestampKey)
    }

    func load(_ record: LibraryBookRecord) {
        guard currentBookID != record.id,
              let fileName = record.localFileName else { return }
        persistPosition()
        let previousRecord = currentRecord
        let previousPosition = position
        if let accountID = previousRecord?.accountLibraryID {
            Task {
                try? await CloudScanClient.configured().saveProgress(bookID: accountID, position: previousPosition)
            }
        }
        removeTimeObserver()
        self.record = record
        self.currentRecord = record
        UserDefaults.standard.set(record.id.uuidString, forKey: "lastPlayedBookID")
        self.lastHandledEventID = nil
        self.activeFilterEvent = nil
        self.skippedEventCount = 0
        self.filterAvailability = FilterAvailability.of(record)
        playbackError = nil
        currentBookID = record.id
        duration = 0
        let url = AudiobookImportService.audioURL(fileName: fileName)
        guard FileManager.default.fileExists(atPath: url.path) else {
            playbackError = "The local audiobook file is missing. Re-import it to listen."
            return
        }
        let item = AVPlayerItem(url: url)
        itemStatusObservation = item.observe(\.status, options: [.new]) { [weak self] item, _ in
            guard item.status == .failed else { return }
            Task { @MainActor in
                self?.isPlaying = false
                self?.playbackError = item.error?.localizedDescription
                    ?? "This audiobook could not be played. It may be protected or use an unsupported audio format."
            }
        }
        player = AVPlayer(playerItem: item)
        position = UserDefaults.standard.double(forKey: positionKey(record.id))
        player?.seek(to: CMTime(seconds: position, preferredTimescale: 600))
        addTimeObserver()
        Task {
            let playable = (try? await item.asset.load(.isPlayable)) == true
            duration = (try? await item.asset.load(.duration).seconds) ?? 0
            if !playable {
                playbackError = "This audiobook could not be decoded by iPhone. Try a non-protected MP3, M4A, or M4B file."
            }
            updateChapter()
            updateNowPlaying()
        }
    }

    func togglePlayback() {
        guard playbackError == nil, let player else { return }
        if isPlaying {
            player.pause()
            isPlaying = false
            persistPosition()
            Task { await syncCurrentProgressToAccount() }
        } else {
            do {
                // The category is set at startup; this only claims the session.
                try AVAudioSession.sharedInstance().setActive(true)
                player.playImmediately(atRate: playbackRate)
                isPlaying = true
            } catch {
                // Previously returned silently, so a refused session looked like the
                // play button simply not working.
                playbackError = "Another app is using audio right now. Try again in a moment."
                return
            }
        }
        updateNowPlaying()
    }

    func seek(to seconds: Double) {
        let target = duration > 0 ? min(max(seconds, 0), duration) : max(seconds, 0)
        lastHandledEventID = nil
        setPosition(target)
        applyContentFilter()
    }

    private func setPosition(_ target: Double) {
        player?.seek(to: CMTime(seconds: target, preferredTimescale: 600))
        position = target
        // A deliberate seek is trusted even when it lands at the very start, unlike a
        // zero observed from a player that has not finished loading.
        persistPosition(allowingRestart: true)
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
        itemStatusObservation = nil
        player?.pause()
        isPlaying = false
    }

    /// Records where the listener is, refusing values the player cannot vouch for.
    ///
    /// This runs twice a second from the time observer. An AVPlayer reports 0 while an
    /// item is still loading and duration is still 0, so an unguarded write here can
    /// replace a real bookmark with the start of the book — the same failure that took
    /// four attempts to pin down on Android. A zero is only believed once the asset has
    /// loaded, or when the listener seeks there deliberately.
    private func persistPosition(allowingRestart: Bool = false) {
        guard let id = currentBookID else { return }
        let defaults = UserDefaults.standard
        if !allowingRestart, position < Self.minimumTrustedPosition, duration <= 0 {
            return
        }
        let stored = defaults.double(forKey: positionKey(id))
        if !allowingRestart, position < Self.minimumTrustedPosition, stored > Self.minimumTrustedPosition {
            return
        }
        defaults.set(max(position, 0), forKey: positionKey(id))
        defaults.set(Date(), forKey: "playbackPositionUpdatedAt.\(id.uuidString)")
    }

    /// Below this, a reported position is indistinguishable from an unloaded player.
    private static let minimumTrustedPosition: Double = 1

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
        // No scan data means nothing can be filtered. That state is published as
        // filterAvailability and shown in the player rather than passing silently,
        // because the audio plays either way and the listener needs to know which.
        guard let events = record?.scanResult?.events else {
            player?.isMuted = false
            activeFilterEvent = nil
            return
        }
        let matching = events.first { event in
            guard event.startTime <= position, position < event.endTime,
                  IOSContentTaxonomy.shouldSkip(event) else { return false }
            return true
        }
        activeFilterEvent = matching
        guard let matching else {
            player?.isMuted = false
            lastHandledEventID = nil
            return
        }
        player?.isMuted = false
        guard lastHandledEventID != matching.id else { return }
        lastHandledEventID = matching.id
        skippedEventCount += 1
        // Duration is 0 until the asset finishes loading asynchronously, and clamping
        // against it in that window turned a skip into a jump to the start of the book.
        let target = matching.endTime + Self.filterExitPadding
        setPosition(duration > 0 ? min(target, duration) : target)
    }

    /// Clears the flagged range before resuming, so its final moment is not replayed.
    private static let filterExitPadding: Double = 0.2

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
