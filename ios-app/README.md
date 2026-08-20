# AudioChoice for iPhone and iPad

Stage 14 introduces the first native SwiftUI version of AudioChoice. It follows
the approved dark interface and green accent direction in
`docs/design/mobile-ui-reference.png`.

## Open and run

1. Open `AudioChoice.xcodeproj` in Xcode.
2. Select the `AudioChoice` scheme.
3. Choose an iPhone simulator or a connected iPhone.
4. If using a physical iPhone, select your Apple Development team under
   **Signing & Capabilities**.
5. Press **Run**.

The app currently uses sample audiobook data so every Stage 14 screen can be
tested without a backend. Selecting **Import** opens Apple's native file picker;
choosing a file demonstrates the scan flow without uploading it.

## Stage 14 screens

- Library and continue listening
- Audiobook import
- Scan progress
- Book details
- Player
- Filter profile
- Collections and profile placeholders

## Stage 15 cloud connection

The import flow now performs a real on-device SHA-256 fingerprint, checks the
private scan catalog, uploads only when needed, submits a scan job, and waits for
the result. Configure the backend URL and API token under **Profile → Cloud
Connection**. Use an HTTPS address that the iPhone can reach.
The access token is stored in the iOS Keychain and is not written to ordinary
app preferences.
Completed scan results are saved locally and their books appear at the front of
the **Recently Added** shelf after returning to the Library tab.

## Stage 16 local library

Selected audiobooks are copied into private Application Support storage before
scanning. AudioChoice extracts embedded title, author, duration, and cover art,
deduplicates imports by SHA-256 fingerprint, reports local storage use, and removes
the private audio and artwork when a library item is deleted.

## Stage 17 playback

The player now uses AVPlayer for real local audiobook playback with persistent
position, background audio, lock-screen controls and artwork, 15/30-second seek,
chapter navigation, playback speeds from 0.75× to 2×, and sleep timers.

## Stage 18 content filtering

Saved scan events now map to the shared taxonomy and drive playback. The active
profile can skip or mute profanity, sexual content, graphic violence, and self-harm.
Book details expose timestamped event review, confidence, playback previews, and a
local correction queue. Custom-word filtering remains unavailable until the backend
publishes privacy-safe word-level events; transcripts never leave the server.

## Stage 19 resilience

The local backend persists fingerprints, scan results, uploads, and job state under
`App_Data/scan-catalog.json`, restoring queued work after restart. The iOS library
persists pending scan IDs and resumes status monitoring after navigation or relaunch.
The importer accepts multiple files, rejects unsafe storage pressure, deduplicates
large files before copying, and exposes explicit scan-update checks from book details.

## Stage 20 accounts

The iOS account screen supports persistent email/password registration and login,
native Sign in with Apple authorization, secure session storage in Keychain, and
sign-out. Google UI and server verification boundaries are present but require the
project’s Google iOS and server OAuth client IDs before activation.

The development business bundle identifier is `com.audiochoice.mobile`. A free Personal
Team may sign local development builds, but no personal name is used in the bundle
identifier, Keychain service, or customer-facing account configuration.

## iOS beta completion

The iOS build now uses only real imported-library data and includes onboarding,
library search, favorites, persistent custom collections, functional chapter and
bookmark screens, privacy disclosures, empty states, and a production-sized
AudioChoice app icon.

The existing local prototype remains unchanged.
