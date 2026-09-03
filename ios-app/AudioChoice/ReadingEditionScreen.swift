import SwiftUI

/// Full-screen reading edition, paired with the audiobook.
///
/// Continuous scrolling rather than pagination, and filtered passages are physically
/// absent from the rendered text rather than styled over — so VoiceOver and any
/// text-extraction path see exactly what the listener sees, which is the point.
struct ReadingEditionScreen: View {
    let record: LibraryBookRecord
    let onClose: () -> Void

    @ObservedObject private var reader = ReadingEditionManager.shared
    @ObservedObject private var playback = AudioPlaybackManager.shared
    @State private var showSettings = false
    @State private var narratedIndex: Int?
    @State private var restoredPosition = false

    private static let scrollSpace = "readingEditionScroll"

    private var palette: ReaderPalette { ReaderPalette.of(reader.settings.theme) }

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider().overlay(palette.mutedInk.opacity(0.2))

            if reader.displayParagraphs.isEmpty {
                Spacer()
                Text(reader.isSyncing ? "Preparing the reading edition…" : "This reading edition has no readable text.")
                    .font(.footnote)
                    .foregroundStyle(palette.mutedInk)
                    .multilineTextAlignment(.center)
                    .padding()
                Spacer()
            } else {
                paragraphList
            }

            Divider().overlay(palette.mutedInk.opacity(0.2))
            transport
        }
        .background(palette.paper.ignoresSafeArea())
        .sheet(isPresented: $showSettings) {
            ReadingSettingsSheet(
                settings: $reader.settings,
                syncMessage: reader.syncMessage,
                isSyncing: reader.isSyncing,
                onResync: { Task { await reader.sync(record: record) } },
                onRemove: {
                    reader.detach(record: record)
                    showSettings = false
                    onClose()
                }
            )
        }
        .task { await reader.open(record: record) }
        // Rebuilt when the filter choices could have changed, never per scroll frame.
        .onChange(of: playback.filterAvailability) { _, _ in reader.rebuildMasks(record: record) }
    }

    // MARK: - Header

    /// Three columns, with the outer two sharing the leftover width equally.
    ///
    /// The close button was previously placed between two Spacers, which only divides the
    /// space the title does not use — so it drifted right as the title grew and never
    /// actually sat where the player's open-book button is. Equal-weight side columns put
    /// it on the real centre line whatever the title's length.
    private var header: some View {
        HStack(spacing: 8) {
            VStack(alignment: .leading, spacing: 1) {
                Text(record.book.title)
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(palette.ink)
                    .lineLimit(1)
                Text(subtitle)
                    .font(.system(size: 11))
                    .foregroundStyle(palette.mutedInk)
                    .lineLimit(1)
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            Button(action: onClose) {
                Image(systemName: "book.closed.fill")
                    .foregroundStyle(ACTheme.accent)
            }
            .accessibilityLabel("Close reading edition")

            Button { showSettings = true } label: {
                Image(systemName: "textformat.size")
                    .foregroundStyle(ACTheme.accent)
            }
            .accessibilityLabel("Reading settings")
            .frame(maxWidth: .infinity, alignment: .trailing)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
    }

    /// Kept to one short line so it cannot crowd the centred close button.
    ///
    /// The removed-passage count used to appear here. Every filtered passage is already
    /// marked in the text where it happens, so the running total added width without
    /// telling the reader anything the page does not.
    private var subtitle: String {
        if let message = reader.syncMessage, reader.timings.isEmpty { return message }
        return "Reading edition"
    }

    // MARK: - Text

    private var paragraphList: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 0) {
                    ForEach(Array(reader.displayParagraphs.enumerated()), id: \.element.paragraph.startCharacter) { index, display in
                        paragraphView(index: index, display: display)
                            .id(index)
                    }
                }
                .padding(.horizontal, reader.settings.margin)
                .padding(.vertical, 16)
            }
            .coordinateSpace(name: Self.scrollSpace)
            // Records the paragraph nearest the top edge so the reading place survives
            // leaving the reader. Only a changed index is persisted, so a scroll does not
            // write on every frame.
            .onPreferenceChange(ReaderTopParagraphKey.self) { top in
                guard let top, restoredPosition else { return }
                reader.savePosition(paragraphIndex: top.index, fraction: 0)
            }
            // Restoring has to wait for the text: the paragraph list is empty while the
            // EPUB is still being extracted, so scrolling on appear would go nowhere.
            .onChange(of: reader.displayParagraphs.count) { _, count in
                guard !restoredPosition, count > 0 else { return }
                restoredPosition = true

                // Where the listening has reached, when the alignment can say. Ten hours into a
                // book this is the only place worth opening at, and it was not being used: the
                // reader restored its own saved paragraph, which is zero for a reader never
                // opened, so it began at the title page with no way to find the story. The
                // follow-audio scroll below could not rescue it either, because it fires on the
                // position changing and a paused book's position does not change.
                // Alignment first. Without it, a proportion of the way through the text, which is
                // wrong by pages and still far better than the title page ten hours in.
                let paragraphs = reader.displayParagraphs.map(\.paragraph)
                let alignedCharacter = ReaderSync.character(at: playback.position, in: reader.timings)
                let character = alignedCharacter ?? ReaderSync.approximateCharacter(
                    atSeconds: playback.position,
                    duration: playback.duration,
                    characterCount: paragraphs.last?.endCharacter ?? 0
                )
                let listening = character.flatMap { paragraphs.indexOfCharacter($0) }
                if reader.settings.followAudio, let listening { narratedIndex = listening }

                // Follow-audio is a promise about where the reader sits, so it decides this too.
                // With it off, the reader keeps its own place and the listener moves it themselves.
                let target = reader.settings.followAudio
                    ? (listening ?? reader.position.paragraphIndex)
                    : reader.position.paragraphIndex
                guard target > 0, target < count else { return }
                proxy.scrollTo(target, anchor: .top)
            }
            .onChange(of: narratedIndex) { _, index in
                // Only follow when the listener is not scrolling themselves, and only
                // when there is somewhere to go.
                guard reader.settings.followAudio, let index else { return }
                withAnimation(.easeInOut(duration: 0.35)) { proxy.scrollTo(index, anchor: .center) }
            }
            .onChange(of: playback.position) { _, seconds in
                guard reader.settings.followAudio, !reader.timings.isEmpty else { return }
                // A gap in coverage keeps the previous highlight rather than snapping the
                // reader back to the start of the book.
                guard let character = ReaderSync.character(at: seconds, in: reader.timings),
                      let index = reader.displayParagraphs.map(\.paragraph).indexOfCharacter(character),
                      index != narratedIndex else { return }
                narratedIndex = index
                reader.savePosition(paragraphIndex: index, fraction: 0)
            }
        }
    }

    private func paragraphView(index: Int, display: ReaderDisplayParagraph) -> some View {
        let isNarrated = reader.settings.followAudio && index == narratedIndex
        return Text(display.displayText)
            .font(reader.settings.font.font(size: reader.settings.fontSize))
            .lineSpacing(reader.settings.lineSpacing)
            .foregroundStyle(palette.ink)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, 4)
            .padding(.vertical, 2)
            .background(
                isNarrated ? ACTheme.accent.opacity(0.16) : Color.clear,
                in: RoundedRectangle(cornerRadius: 6)
            )
            .padding(.bottom, 12)
            .background(
                // Only visible rows evaluate this, because the list is lazy.
                GeometryReader { geometry in
                    Color.clear.preference(
                        key: ReaderTopParagraphKey.self,
                        value: ReaderTopParagraph(
                            index: index,
                            distanceFromTop: geometry.frame(in: .named(Self.scrollSpace)).minY
                        )
                    )
                }
            )
            .contentShape(Rectangle())
            .onTapGesture {
                guard reader.settings.followAudio else { return }
                guard let seconds = ReaderSync.time(
                    forCharacter: display.paragraph.startCharacter,
                    in: reader.timings
                ) else { return }
                playback.seek(to: seconds)
                reader.savePosition(paragraphIndex: index, fraction: 0)
            }
            // Names an otherwise undiscoverable gesture.
            .accessibilityHint(reader.settings.followAudio ? "Plays the audiobook from here" : "")
    }

    // MARK: - Transport

    private var transport: some View {
        HStack(spacing: 34) {
            Button { playback.skip(by: -30) } label: {
                Image(systemName: "gobackward.30").font(.title2).foregroundStyle(palette.ink)
            }
            .accessibilityLabel("Back 30 seconds")

            Button { playback.togglePlayback() } label: {
                Image(systemName: playback.isPlaying ? "pause.circle.fill" : "play.circle.fill")
                    .font(.system(size: 52))
                    .foregroundStyle(ACTheme.accent)
            }
            .accessibilityLabel(playback.isPlaying ? "Pause" : "Play")

            Button { playback.skip(by: 30) } label: {
                Image(systemName: "goforward.30").font(.title2).foregroundStyle(palette.ink)
            }
            .accessibilityLabel("Forward 30 seconds")
        }
        .padding(.vertical, 12)
    }
}

/// Text size, margins, theme, typeface and whether the text follows the narration.
struct ReadingSettingsSheet: View {
    @Binding var settings: ReaderSettings
    var syncMessage: String?
    var isSyncing: Bool
    var onResync: () -> Void
    var onRemove: () -> Void
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            Form {
                Section("Text size") {
                    Picker("Text size", selection: $settings.fontScale) {
                        ForEach(ReaderSettings.fontScales, id: \.self) { scale in
                            Text(ReaderSettings.fontScaleLabel(scale)).tag(scale)
                        }
                    }
                    .pickerStyle(.segmented)
                }

                Section {
                    Picker("Typeface", selection: $settings.font) {
                        ForEach(ReaderFont.allCases) { font in
                            // Each option renders in the face it selects, so the choice can
                            // be judged by eye rather than by name.
                            Text(font.title).font(font.font(size: 15)).tag(font)
                        }
                    }
                    .pickerStyle(.inline)
                } header: {
                    Text("Typeface")
                } footer: {
                    Text("OpenDyslexic weights the bottom of each letter and varies similar shapes, which can make characters harder to transpose or flip.")
                }

                Section("Margins") {
                    Picker("Margins", selection: $settings.marginScale) {
                        ForEach(ReaderSettings.marginScales, id: \.self) { scale in
                            Text(ReaderSettings.marginScaleLabel(scale)).tag(scale)
                        }
                    }
                    .pickerStyle(.segmented)
                }

                Section("Theme") {
                    Picker("Theme", selection: $settings.theme) {
                        ForEach(ReaderTheme.allCases) { theme in
                            Text(theme.title).tag(theme)
                        }
                    }
                    .pickerStyle(.segmented)
                }

                Section {
                    Toggle("Follow the audiobook", isOn: $settings.followAudio)
                } footer: {
                    Text("Highlights and scrolls to the passage being narrated, and lets you tap a paragraph to jump the audio there.")
                }

                Section {
                    Button(isSyncing ? "Syncing…" : "Re-sync with the audiobook", action: onResync)
                        .disabled(isSyncing)
                    Button("Remove reading edition", role: .destructive, action: onRemove)
                } header: {
                    Text("Reading edition")
                } footer: {
                    // The real reason a sync failed, rather than one vague message covering
                    // a network problem, an expired session and a book with no transcript.
                    if let syncMessage { Text(syncMessage) }
                }
            }
            .navigationTitle("Reading settings")
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done") { dismiss() }
                }
            }
        }
    }
}

/// A candidate for "which paragraph is the listener looking at".
struct ReaderTopParagraph: Equatable {
    let index: Int
    /// Offset of the paragraph's top edge from the top of the scroll viewport. Negative
    /// once it has scrolled above the edge.
    let distanceFromTop: Double
}

/// Reduces the visible paragraphs to whichever sits nearest the top of the viewport.
///
/// Only rows the lazy stack has materialised participate, so this stays proportional to
/// what is on screen rather than to the length of the book.
struct ReaderTopParagraphKey: PreferenceKey {
    static let defaultValue: ReaderTopParagraph? = nil

    static func reduce(value: inout ReaderTopParagraph?, nextValue: () -> ReaderTopParagraph?) {
        guard let next = nextValue() else { return }
        guard let current = value else { value = next; return }
        value = nearerTheTop(current, next)
    }

    /// The paragraph occupying the top of the viewport is the last one whose edge has
    /// scrolled past it. Falling back to the first one below keeps a result at the very
    /// start of the book, where nothing has scrolled past yet.
    private static func nearerTheTop(
        _ first: ReaderTopParagraph,
        _ second: ReaderTopParagraph
    ) -> ReaderTopParagraph {
        switch (first.distanceFromTop <= 0, second.distanceFromTop <= 0) {
        case (true, true):
            return first.distanceFromTop >= second.distanceFromTop ? first : second
        case (true, false):
            return first
        case (false, true):
            return second
        case (false, false):
            return first.distanceFromTop <= second.distanceFromTop ? first : second
        }
    }
}
