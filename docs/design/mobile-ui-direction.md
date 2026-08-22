# AudioChoice mobile UI direction

The companion image `mobile-ui-reference.png` is the visual source of truth for the
initial iOS and Android interfaces.

## Visual system

- Near-black backgrounds with subtly lighter cards and panels.
- Bright AudioChoice green for primary actions, progress, active states, and branding.
- White primary text and restrained gray secondary text.
- Rounded cards, thin neutral borders, generous spacing, and clean iconography.
- Focused layouts with one obvious primary action per screen.

## Reference screens

1. Library: continue listening, recently added books, collections, and bottom navigation.
2. Import: drag-and-drop where supported, file browser, supported formats, and privacy note.
3. Intelligence: visible metadata, fingerprint, library lookup, download, and readiness steps.
4. Book details: cover, metadata, verification, play/continue, chapters, notes, bookmarks, and filters.
5. Player: large cover art, chapter context, progress, playback controls, speed, chapters, sleep timer, and bookmarks.
6. Filters: active profile, category toggles, severity summaries, custom words, and profile management.

## Platform adaptation

- Preserve the same information hierarchy and brand treatment on both platforms.
- Use native SwiftUI and Jetpack Compose controls, navigation, accessibility, and system behavior.
- On iPhone, replace desktop drag-and-drop emphasis with the native document picker while retaining drag-and-drop on iPad where appropriate.
- Never display or expose transcripts. Intelligence progress describes processing stages only.
- Update the import privacy copy for the cloud architecture: fingerprints and scan metadata are checked first; audio uploads only when a scan is unavailable.
