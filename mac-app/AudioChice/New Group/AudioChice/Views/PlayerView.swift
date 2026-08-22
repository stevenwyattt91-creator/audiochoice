import SwiftUI

struct PlayerView: View {

    let book: Book

    @ObservedObject var playback: PlaybackService

    @Binding var sliderPosition: Double
    @State private var showingChapterList = false
    @State private var showingBookmarks = false
    @State private var showingContentControls = false

    let onBack: () -> Void

    var body: some View {
        
        ZStack {
            
            Color.black.opacity(0.97)
                .ignoresSafeArea()
            
            ScrollView {
                
                VStack(spacing: 28) {
                    
                    PlayerHeaderView(
                        onBack: onBack,
                        onBookmark: {
                            showingBookmarks = true
                        }
                    )
                    
                    BookCoverView(
                        book: book,
                        height: 380
                    )
                    .frame(maxWidth: 320)
                    
                    VStack(spacing: 8) {
                        
                        Text(
                            book.identity?.workTitle ?? book.title
                        )
                        .font(.system(size: 30, weight: .bold))
                        .multilineTextAlignment(.center)
                        
                        if let author = book.author {
                            
                            Text(author)
                                .font(.title3)
                                .foregroundStyle(.secondary)
                        }
                        
                        Text(currentChapter)
                            .foregroundStyle(.green)
                    }
                    
                    PlaybackProgressView(
                        sliderPosition: $sliderPosition,
                        currentTime: playback.currentTime,
                        duration: playback.duration,
                        onSeek: { time in
                            playback.seek(to: time)
                        }
                    )
                    .frame(maxWidth: 720)
                    
                    PlaybackControlsView(
                        
                        playing: playback.isPlaying,
                        
                        back60: {
                            playback.skipBackward(seconds: 60)
                        },
                        
                        back10: {
                            playback.skipBackward(seconds: 10)
                        },
                        
                        playPause: {
                            playback.togglePlayPause()
                        },
                        
                        forward10: {
                            playback.skipForward(seconds: 10)
                        },
                        
                        forward60: {
                            playback.skipForward(seconds: 60)
                        }
                    )
                    
                    SecondaryControlsView(
                        
                        sleep: {
                        },
                        
                        chapters: {
                            showingChapterList = true
                        },
                        
                        notes: {
                            showingBookmarks = true
                        },
                        
                        filters: {
                            showingContentControls = true
                        }
                    )
                    
                    PlaybackSpeedView(
                        
                        selected: Double(
                            playback.playbackSpeed
                        )
                        
                    ) { speed in
                        
                        playback.setPlaybackSpeed(
                            Float(speed)
                        )
                    }
                    
                    if let error = playback.playbackError {
                        
                        Label(
                            error,
                            systemImage: "exclamationmark.triangle.fill"
                        )
                        .foregroundStyle(.orange)
                    }
                }
                .padding(40)
            }
        }
        .onChange(of: playback.currentTime) {
            
            sliderPosition = playback.currentTime
        }
        .sheet(isPresented: $showingChapterList) {
            
            ChapterListView(
                
                book: book,
                
                currentTime: playback.currentTime
                
            ) { selectedTime in
                
                playback.seek(to: selectedTime)
            }
        }
        .sheet(isPresented: $showingBookmarks) {
            BookmarkListView(
                book: book,
                currentTime: playback.currentTime,
                onAdd: { position, title, note in
                    // We will connect this to LibraryManager next.
                },
                onSelect: { selectedTime in
                    playback.seek(to: selectedTime)
                    sliderPosition = selectedTime
                },
                onDelete: { bookmarkID in
                    // We will connect this to LibraryManager next.
                }
            )
            .sheet(isPresented: $showingContentControls) {

                ContentControlsView(
                    filterManager: FilterManager(
                        profile: FilterPreviewFactory.makePreviewProfile()
                    )
                )
            }
        }
        }

    private var currentChapter: String {

        guard !book.chapters.isEmpty else {

            return "Audiobook"
        }

        return book.chapters.last {

            $0.startTime <= playback.currentTime

        }?.title

        ?? book.chapters.first?.title

        ?? "Audiobook"
    }
}

#Preview {

    PlayerView(

        book: Book(
            title: "Preview",
            originalFileURL: URL(fileURLWithPath: "/"),
            fileType: "mp3"
        ),

        playback: PlaybackService(),

        sliderPosition: .constant(0),

        onBack: {}
    )
}
