# AudioChoice for iPhone and iPad

The iOS target follows the Android beta's shared scan contract. It follows
the approved dark interface and green accent direction in
`docs/design/mobile-ui-reference.png`.

## Open and run

1. Open `AudioChoice.xcodeproj` in Xcode.
2. Select the `AudioChoice` scheme.
3. Choose an iPhone simulator or a connected iPhone.
4. If using a physical iPhone, select your Apple Development team under
   **Signing & Capabilities**.
5. Press **Run**.

The importer does not restrict users to a beta title allowlist. It accepts normal
audio files and sends the same fingerprint-first request to the `ios-beta` server
lane used by the Android beta. A matching scan is reused; only an unmatched file
is uploaded for processing.

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

The iOS beta uses the backend's Lambda transcript lane. The backend's full filter
pipeline handles sexual-content candidates through Terra and Sol; that processing
does not run on the phone and is not duplicated in the iOS client.

## Stage 19 resilience

The local backend persists fingerprints, scan results, uploads, and job state under
`App_Data/scan-catalog.json`, restoring queued work after restart. The iOS library
persists pending scan IDs and resumes status monitoring after navigation or relaunch.
The importer accepts multiple files, rejects unsafe storage pressure, deduplicates
large files before copying, and exposes explicit scan-update checks from book details.

## Stage 20 accounts

The iOS account screen supports persistent email/password registration and login,
native Sign in with Apple authorization, secure session storage in Keychain, Google
sign-in, and sign-out. Before building, replace these two Xcode build settings in
both Debug and Release with the Google Cloud **iOS** OAuth client values:

- `GOOGLE_IOS_CLIENT_ID`: the iOS OAuth client ID for `com.audiochoice.mobile`.
- `GOOGLE_REVERSED_CLIENT_ID`: the reversed form of that same iOS client ID, used
  as the callback URL scheme.

The server/audience client ID is already configured in `AppInfo.plist` and must
remain the Web application OAuth client used by the backend.

The computer-to-phone handoff supports both `audiochoice://transfer/...` and
`audiochoice-beta://transfer/...`, including scanning the QR code in the Import
screen. AAX DRM recovery is intentionally not duplicated on iOS; use an authorized
converter and import the resulting M4B. The iOS app never bypasses audiobook DRM.

The development business bundle identifier is `com.audiochoice.mobile`. A free Personal
Team may sign local development builds, but no personal name is used in the bundle
identifier, Keychain service, or customer-facing account configuration.

## iOS beta completion

The iOS build now uses only real imported-library data and includes onboarding,
library search, favorites, persistent custom collections, functional chapter and
bookmark screens, privacy disclosures, empty states, and a production-sized
AudioChoice app icon.

The existing local prototype remains unchanged.
