# Implementation Plan

Derived from `.kiro/specs/epub-narration/requirements.md` and `.kiro/specs/epub-narration/design.md`.

Ordering is load-bearing. The experimental gate lands first so no narration surface can leak into a
beta or release build at any point during implementation. The pure core (extraction, structure
parsing, plan construction, the filtered-range merge, the scheduler function, the timeline
arithmetic) lands next, because it carries the correctness properties and needs neither a device nor
a network. `PlayerViewModel` is shipped code that imported audiobooks depend on, so the
`PlaybackTimeline` indirection and its two regression guards land before any narration playback is
built on them. Values the design requires to be measured are fixed by benchmark tasks near the end,
and the tasks that consume those values say so explicitly.

Tension references point at the numbered entries in
`design.md#tensions-between-the-requirements-and-the-existing-code`.

---

## 1. Experimental gate and configuration

- [x] 1. Gate the feature before any narration code exists
- [x] 1.1 Add `NarrationConfig` as the single reader of the experimental flag
  - Create `android-app/app/src/main/java/com/audiochoice/mobile/narration/NarrationConfig.kt` with
    `val enabled: Boolean get() = BuildConfig.EXPERIMENTAL_BUILD`, mirroring the shape of
    `com/audiochoice/mobile/beta/BetaConfig.kt`.
  - This feature is the first consumer of `EXPERIMENTAL_BUILD`, which is declared in
    `android-app/app/build.gradle.kts` but read nowhere today (design tension 8), so there is no
    existing gating pattern to follow beyond `BetaConfig`.
  - _Requirements: R19.1, R19.2, R19.4_
- [x] 1.2 Advance the experimental cycle identifier
  - Change the `experimental` build type's `BETA_VERSION` build config field in
    `android-app/app/build.gradle.kts` from `"Experimental 1"` to the next cycle identifier. Add no
    build type and no product flavour.
  - _Requirements: R19.4, R19.5_
- [x] 1.3 Write gate tests
  - Add `android-app/app/src/test/java/com/audiochoice/mobile/narration/NarrationConfigTest.kt`
    asserting `enabled` tracks `BuildConfig.EXPERIMENTAL_BUILD`, and a test that enumerates the
    navigation routes and library import actions to assert no narration route or action is
    registered while `enabled` is false.
  - Assert the narration storage root resolves under the application-id-suffixed `filesDir`, so a
    beta or release install on the same device cannot read or alter narration data.
  - _Requirements: R19.2, R19.3, R19.6_

## 2. Narration data models and the on-disk store

- [x] 2. Persist plans, queues and timelines as files keyed by the Source_EPUB SHA-256
- [x] 2.1 Declare the device-only narration models
  - Create `android-app/app/src/main/java/com/audiochoice/mobile/data/NarrationModels.kt` holding
    `NarrationPlan` (with `PLAN_VERSION = 1`), `PlanInputs`, `NarrationChapter`, `NarrationUnit`,
    `RenderState`, `RenderQueue`, `SelectedVoice`, `VoiceKind` and `PronunciationRule`, all
    `@Serializable`. These never cross the wire, so they do not go in `android-contract`.
  - _Requirements: R4.7, R4.13, R6.8, R8.1, R14.1, R14.4_
- [x] 2.2 Implement `NarrationStore` file layout and atomic writes
  - Create `android-app/app/src/main/java/com/audiochoice/mobile/narration/NarrationStore.kt` owning
    `filesDir/narration/<sha256>/` with `book-text.txt`, `plan.json`, `render-queue.json`,
    `text-scan.json`, `timeline/<index>.json` and `audio/chapter_<index>.m4a`.
  - Write every JSON file to a `.tmp` sibling and rename, so process death mid-write cannot leave a
    half-parsed plan. Store Chapter_Timelines chapter-relative so re-rendering one chapter never
    invalidates another chapter's file.
  - _Requirements: R1.9, R4.6, R16.4, R19.6_
- [x] 2.3 Implement plan version and Book_Text hash handling on load
  - In `NarrationStore`: a Plan_Version mismatch discards the plan and keeps Text_Scan_Events; a
    Book_Text hash mismatch discards the plan and requires a new Text_Scan; a deserialisation
    failure discards the plan and keeps the library entry and content URI.
  - _Requirements: R4.9, R4.10, R4.12_
- [x] 2.4 Add the narration DataStore keys and extend removal
  - Add to the existing `local_audio_files` preferences store in
    `android-app/app/src/main/java/com/audiochoice/mobile/data/LocalAudioStore.kt` the keys
    `narration_voice_<sha>`, `narration_flags_<sha>`, `narration_pronunciations_<sha>`,
    `narration_pronunciations_account`, `narration_tier`, `narration_tier_read_at`,
    `narration_tier_plan`, `narration_text_scan_ack`, `narration_premium_ack`,
    `neural_voice_rate`, `neural_voice_model_version`.
  - Extend `LocalAudioStore.remove(sha256)` to delete the book-scoped keys and the whole
    `narration/<sha256>/` directory, keeping the account-scoped rules.
  - _Requirements: R1.9, R5.14, R7.10, R8.9, R11.6, R14.4, R14.5, R16.7, R16.9_
- [x] 2.5 Add a property-test dependency
  - Add a property-based testing library (jqwik or kotest-property) to
    `android-app/gradle/libs.versions.toml` and as `testImplementation` in
    `android-app/app/build.gradle.kts`. The project has only JUnit 4 today, and several requirements
    are stated as properties over all inputs.
  - _Requirements: R4.7, R4.8, R4.11_
- [x] 2.6 Write store round-trip and performance tests
  - Add `android-app/app/src/test/java/com/audiochoice/mobile/narration/NarrationStoreTest.kt` with
    a property test that deserialising a serialised plan yields a structurally equal plan including
    every `PlanInputs` member, a property test that a timeline round-trips with offsets exact and
    times within 1 ms, and a timed test that a 20,000-unit plan serialises and deserialises within
    2.0 seconds each.
  - _Requirements: R4.6, R4.7, R4.8_

## 3. EPUB narration extraction

- [x] 3. Add a narration extraction entry point that records offsets, structure and encryption
- [x] 3.1 Add `readNarrationDocument` alongside the existing `read`
  - Extend `android-app/app/src/main/java/com/audiochoice/mobile/reader/EpubTextReader.kt` with
    `readNarrationDocument` returning `EpubDocument` (text, extraction version, language, title,
    author, cover entry, `ResourceSpan` list, non-prose spans, `NavigationOutline`, encrypted
    entries). Emit non-prose spans and resource spans from the same `StringBuilder` pass that
    produces the text, recording builder length before and after each marked element, and record an
    `anchorOffsets` map keyed `"entryName#id"` so a navigation fragment resolves to an exact offset.
  - Do not call `trimFrontMatter` on this path (design tension 1): front matter is retained in
    Book_Text and classified in task 5.3. Leave `read()` and its trimming untouched so cached
    reader alignments for imported audiobooks are unaffected.
  - _Requirements: R2.1, R3.4, R3.6, R3.13_
- [x] 3.2 Parse `META-INF/encryption.xml` and classify resources
  - Collect every `CipherReference/@URI` into `encryptedEntries`, classify each entry as a
    Text_Resource (package document, EPUB 3 nav, NCX, spine document) or a Non_Text_Resource, and
    report Store_DRM when any Text_Resource is encrypted, including ADEPT_Encryption over a
    Text_Resource with no separate branch. Treat encryption over Non_Text_Resources only, and
    font obfuscation, and a missing `encryption.xml`, as no encryption, and exclude those entries
    from extraction. Read no `CipherData` payload and attempt no decryption.
  - Run this pass before any spine document is converted.
  - _Requirements: R2.2, R2.3, R2.4, R2.5, R2.13_
- [x] 3.3 Extract package metadata and the cover entry
  - Read the first `dc:title` and first `dc:creator` in document order, the declared language, and
    the manifest cover image entry name, in `EpubTextReader`.
  - _Requirements: R1.4, R1.6_
- [x] 3.4 Add the extraction version and the Book_Text hash
  - Add an `EXTRACTION_VERSION` constant starting at 1, cache Book_Text to
    `narration/<sha256>/book-text.txt` rather than to `LocalAudioStore`'s `epub_text` directory so
    the two extraction profiles cannot overwrite each other, and compute the Book_Text hash that
    `PlanInputs` records.
  - _Requirements: R4.10, R16.7_
- [x] 3.5 Write extraction tests
  - Add `android-app/app/src/test/java/com/audiochoice/mobile/reader/EpubNarrationExtractionTest.kt`
    with EPUB fixtures covering an ADEPT-encrypted spine document, a font-obfuscated-only archive,
    an image-only encrypted archive and a plain archive. Assert Book_Text is byte-identical across
    runs and across process restarts at a fixed extraction version, that offsets in `ResourceSpan`
    and `nonProseSpans` index Book_Text exactly, and that `read()` output is unchanged.
  - _Requirements: R2.2, R2.3, R2.5, R2.13_

## 4. EPUB validation

- [x] 4. Decide acceptance with one reported reason and no retained text on decline
- [x] 4.1 Implement `EpubValidator` with the mandated ordering
  - Create `android-app/app/src/main/java/com/audiochoice/mobile/narration/EpubValidator.kt`
    evaluating, in order: content URI or ZIP central directory unreadable; `container.xml` absent or
    naming no present package document; Store_DRM; no parseable unencrypted spine document; fewer
    than 500 letters or digits in Book_Text. Report exactly one reason. Run off the main thread.
  - _Requirements: R2.1, R2.6, R2.7, R2.8, R2.9, R2.10, R2.17_
- [x] 4.2 Purge extracted data on every decline
  - On any decline, delete every extracted character and archive resource, including the package and
    navigation documents read during the Store_DRM classification, create no library entry, persist
    no plan, and release any read permission taken on the file.
  - _Requirements: R2.11, R2.12_
- [x] 4.3 Build the Store_DRM decline surface
  - Add the decline UI under `android-app/app/src/main/java/com/audiochoice/mobile/narration/ui/`
    naming which of the package document, navigation document and spine documents are encrypted,
    naming at least three sources of DRM-free EPUBs, stating the Amazon Manage Your Content and
    Devices route for DRM-free Kindle purchases with a control that opens that page, stating that a
    Kindle Unlimited borrow offers no EPUB download, and stating that DRM is the publisher's or
    author's choice.
  - Content model, copy and its tests are complete in `DeclineMessage.kt`. Compose rendering is
    deferred to task 17 where the import screen exists to host it, so the surface is not built
    without an entry point.
  - _Requirements: R2.5, R2.14, R2.15, R2.16_
- [x] 4.4 Write validator tests
  - Add `android-app/app/src/test/java/com/audiochoice/mobile/narration/EpubValidatorTest.kt` with
    one fixture per branch of the ordering, asserting exactly one reason per file, asserting the
    ordering when two conditions hold at once, asserting the 5.0-second bound for a 100 MB file, and
    asserting no extracted character or resource survives a decline.
  - _Requirements: R2.10, R2.11, R2.12, R2.17_

## 5. Structure parsing and non-prose classification

- [x] 5. Divide Book_Text into chapters and mark what must not be spoken
- [x] 5.1 Implement chapter derivation and the spine fallback
  - Create `android-app/app/src/main/java/com/audiochoice/mobile/narration/StructureParser.kt`
    deriving one chapter per top-level `toc` nav entry, else per top-level NCX `navPoint`, else per
    spine document contributing at least one character. Ignore nested entries and entries resolving
    to no spine document. Fall back to spine derivation and record the fallback against the plan
    when navigation cannot be parsed, resolves no entry, or yields more than 2,000 chapters.
  - _Requirements: R3.1, R3.2, R3.3, R3.12_
- [x] 5.2 Implement ordering, boundary closure and titles
  - Order by spine order then ascending start offset. Close boundaries mechanically: sort starts,
    set each end to the next start, set the last end to `Book_Text.length`, set the first start to
    0, drop empty ranges, so coverage and non-overlap hold by construction. Trim titles, collapse
    internal whitespace runs to one space, truncate to 200 characters, and fall back to the 1-based
    ordinal when the source supplies no usable title.
  - _Requirements: R3.4, R3.5, R3.7, R3.11_
- [x] 5.3 Consume the non-prose spans and classify front matter
  - Mark `table`, `pre`, `code`, `figcaption` and `img`; the EPUB structural semantics `footnote`,
    `endnote`, `pagebreak`, `noteref`, `toc`, `cover`, `titlepage`, `copyright-page`, `colophon`,
    `landmarks`, `loi`, `lot`; and the ARIA roles `doc-footnote`, `doc-endnote`, `doc-pagebreak`,
    extending each region over the element's descendants. Apply the structural-semantics rule at
    spine-document granularity so a whole front-matter document is non-prose.
  - _Requirements: R3.6, R3.13_
- [x] 5.4 Persist chapters as `AudioChapter`, including unrendered chapters
  - Regenerate the `AudioChapter` list on every Chapter_Audio completion: a rendered chapter gets
    its real `startSeconds`/`endSeconds`, an unrendered chapter gets a zero-length entry at the
    current Narration_Duration. `AudioChapter` has no representation for an unrendered chapter
    (design tension 5); the zero-length entry is what makes the existing `previousChapter`,
    `nextChapter` and `sleepAtEndOfChapter` logic behave correctly with no change to it.
  - _Requirements: R3.8_
- [x] 5.5 Write structure parser tests
  - Add `android-app/app/src/test/java/com/audiochoice/mobile/narration/StructureParserTest.kt`
    with a property test that chapter ranges are ordered, non-overlapping, each at least one
    character, and together cover every offset; example tests for each derivation source and each
    fallback trigger; a test that a fragment-targeted navigation entry splits one spine document
    into two chapters; and a timed test completing within 5.0 seconds for 1,000,000 characters off
    the main thread.
  - _Requirements: R3.5, R3.9, R3.10, R3.12_

## 6. Plan construction and unit segmentation

- [x] 6. Build the Narration_Plan whose units index Book_Text exactly
- [x] 6.1 Implement the three-level segmentation
  - In `StructureParser`, split prose at sentence boundaries with
    `java.text.BreakIterator.getSentenceInstance` for the Book_Text_Language, split an over-long
    sentence at clause boundaries (comma, semicolon, colon, en dash, em dash followed by
    whitespace), and split an over-long clause at the last word boundary at or before the limit.
    Set Synthesis_Input_Limit to `min(1000, TextToSpeech.getMaxSpeechInputLength())`, read once and
    recorded in `PlanInputs`.
  - _Requirements: R4.1_
- [x] 6.2 Segment over prose sub-ranges only and hold the unit invariants
  - Subtract merged non-prose spans from each chapter range and segment the remainder, so an
    overlapping unit cannot be constructed. Record `(start, end)` plus
    `sourceCharacters == Book_Text.substring(start, end)`. Emit no unit whose Spoken_Text lacks a
    letter or digit.
  - _Requirements: R4.2, R4.3, R4.4, R4.5_
- [x] 6.3 Handle empty chapters and an empty plan
  - Record a chapter whose prose yields no unit with zero units and as requiring no rendering.
    Persist no plan at all when every chapter would hold zero units, report that the Source_EPUB
    contains no narratable prose, and keep the book unrendered.
  - _Requirements: R4.13, R4.14_
- [x] 6.4 Assemble `PlanInputs` and keep construction pure
  - Record the Source_EPUB SHA-256, Book_Text hash, extraction version, Plan_Version,
    Synthesis_Input_Limit, enabled event keys and a Pronunciation_Rule fingerprint. Admit no random
    ordering, hash-set iteration or time-dependent input into segmentation.
  - _Requirements: R4.11_
- [x] 6.5 Write plan property tests
  - Add `android-app/app/src/test/java/com/audiochoice/mobile/narration/NarrationPlanTest.kt` with
    property tests over generated Book_Text for: `substring(start, end) == sourceCharacters`; units
    ordered, non-empty, within their chapter, non-overlapping with `end <= next.start`; no unit
    overlapping a non-prose span by one character; every unit holding a letter or digit; and equal
    plans across runs for the same Book_Text and `PlanInputs`.
  - _Requirements: R4.2, R4.3, R4.4, R4.5, R4.11_

## 7. Filtered-range derivation and synthesis exclusion

- [x] 7. Remove filtered characters before anything is spoken, stored or sent
- [x] 7.1 Derive Filtered_Ranges by reusing `ReaderMasking`
  - In a new `android-app/app/src/main/java/com/audiochoice/mobile/narration/FilteredRanges.kt`, map
    Enabled_Text_Scan_Event Source_Ranges to `ReaderMask` and merge with the existing
    `List<ReaderMask>.merged()` in `com/audiochoice/mobile/reader/ReaderMasking.kt`, whose
    `next.start <= previous.end` rule already merges the touching case. Reusing `ReaderMask`
    guarantees the reader and the renderer agree on what is filtered.
  - _Requirements: R6.1_
- [x] 7.2 Build Spoken_Text with filtered characters excluded
  - Add `SpokenTextBuilder` handling: a unit covered in full is omitted from the Render_Queue with
    no submission and no timeline entry; a partly covered unit submits its uncovered characters
    concatenated in ascending order with one space at each removal boundary and records one timeline
    entry spanning the whole unit; uncovered characters with no letter or digit are treated as
    covered in full; no enabled event means every unit is submitted unchanged; a chapter whose every
    unit is omitted writes no audio, records an empty timeline, becomes rendered, and adds 0.0
    seconds to Narration_Duration.
  - _Requirements: R6.2, R6.4, R6.5, R6.7, R6.11, R6.12_
- [x] 7.3 Halt rendering on an out-of-range event offset
  - Submit no unit of that book, report that the filter results cannot be applied, and offer to
    request the Text_Scan again.
  - _Requirements: R6.10_
- [x] 7.4 Record omission counts per chapter
  - Persist, through `NarrationStore`, the per-chapter count of units omitted in full and the count
    of units from which characters were removed, in `RenderQueue`.
  - _Requirements: R6.8_
- [x] 7.5 Write filtered-range tests
  - Add `android-app/app/src/test/java/com/audiochoice/mobile/narration/FilteredRangesTest.kt` with
    a property test that a superset of enabled events yields a Narration_Duration no greater than a
    subset does, a timed test completing the merge within 1.0 second for 1,000,000 characters and
    10,000 events, and example tests for the touching-range merge and each exclusion case.
  - _Requirements: R6.1, R6.6, R6.11_

## 8. The render scheduler

- [x] 8. Make the whole render policy one pure function
- [x] 8.1 Implement `nextChapterToRender`
  - Create `android-app/app/src/main/java/com/audiochoice/mobile/narration/NarrationRenderScheduler.kt`
    with a pure `nextChapterToRender(states, playheadChapter, renderAheadWindow, fullBookRequested,
    pausedByListener)`. Count `readyAhead` as the contiguous run of rendered chapters after the
    playhead, not the total rendered anywhere after it. Treat `RENDER_FAILED` as distinct from
    `NOT_RENDERED` so a failed chapter neither blocks the scheduler nor retries itself.
  - Read the Render_Ahead_Window from configuration with no value hard-coded; the value is fixed by
    task 27.3 from the measurements, and is at least 1 until then.
  - _Requirements: R11.1, R11.3, R11.4, R11.7, R11.16, R11.21_
- [x] 8.2 Write scheduler table tests
  - Add
    `android-app/app/src/test/java/com/audiochoice/mobile/narration/NarrationRenderSchedulerTest.kt`
    as a table test over states, playhead, window, full-book flag and pause flag, covering the
    window boundary, a rendered gap ahead of the playhead, a failed chapter being stepped past, and
    the pause request returning no chapter.
  - _Requirements: R11.3, R11.4, R11.7, R11.14, R11.16_

## 9. Book_Time arithmetic

- [x] 9. Turn per-chapter audio into one continuous position space
- [x] 9.1 Implement `NarrationTimeline`
  - Create `android-app/app/src/main/java/com/audiochoice/mobile/narration/NarrationTimeline.kt`
    with `RenderedChapter(planIndex, bookStartMs, durationMs, timings)`, `totalDurationMs` over
    rendered chapters only, `bookTimeMs(itemIndex, positionInItemMs)`, `locate(bookTimeMs)` by
    binary search over `bookStartMs`, and `narrationTimingRanges` offsetting each chapter-relative
    timing into Book_Time. Apply the cumulative offset here, at load, so stored timelines stay
    chapter-relative.
  - _Requirements: R12.2, R12.13_
- [x] 9.2 Write timeline property tests
  - Add `android-app/app/src/test/java/com/audiochoice/mobile/narration/NarrationTimelineTest.kt`
    with a property test that an offset converted by `readerTimeForCharacter` and back by
    `readerCharacterForTime` lands within the Source_Range of the unit containing the original
    offset, a property test that `narrationTimingRanges` is ordered by both start time and start
    character, and a test that `locate` inverts `bookTimeMs` at every chapter boundary.
  - _Requirements: R12.12, R12.13_

## 10. The voice engine seam and the system voice

- [x] 10. Render one chapter at a time behind one interface
- [x] 10.1 Declare the `VoiceEngine` seam
  - Create `android-app/app/src/main/java/com/audiochoice/mobile/narration/voice/VoiceEngine.kt` with
    `VoiceKind`, `SpokenUnit`, `ChapterRenderRequest`, `ChapterRenderOutcome`
    (`Rendered`/`Failed(retryable)`/`Cancelled`) and `VoiceEngine` carrying `maximumInputCharacters`.
    The seam is per chapter, not per unit, so timelines are built in exactly one place.
  - _Requirements: R8.19_
- [x] 10.2 Implement `SystemVoiceEngine`
  - Create `.../narration/voice/SystemVoiceEngine.kt` wrapping `TextToSpeech.synthesizeToFile`, one
    call per unit, bridging `UtteranceProgressListener` into a `suspendCancellableCoroutine`, at
    fixed rate 1.0 and pitch 1.0 so the Player's speed control stays the only place speed is set.
    Make no network request. Record the engine's default voice for the Book_Text_Language as the
    Selected_Voice at import, and expose the engine's voices for that language for selection.
  - _Requirements: R8.1, R8.2, R8.3, R8.11_
- [x] 10.3 Encode one chapter file with exact per-unit boundaries
  - Feed each unit's PCM through a single `MediaCodec` AAC-LC encoder into a `MediaMuxer` at 24 kbps
    mono, taking per-unit boundary times from the running sample count as each unit is appended
    rather than measuring them afterwards, and emit chapter-relative `ReaderTimingRange` values.
  - _Requirements: R12.12, R13.5_
- [x] 10.4 Split Spoken_Text that exceeds the engine's input ceiling
  - After Pronunciation_Rules are applied, submit an over-long Spoken_Text as consecutive requests
    split at word boundaries and still record one timeline entry spanning the whole unit. Check
    after rule application, because applying a rule can lengthen text.
  - _Requirements: R8.19_
- [x] 10.5 Handle engine and synthesis failures
  - Report no installed engine or no successful init within 5.0 seconds by offering the Android
    text-to-speech settings screen and keeping the book unrendered; report no voice for the
    Book_Text_Language by naming the declared language and offering that screen; retry a synthesis
    error or a missing audio file within 30.0 seconds up to two times, then record the chapter as
    render failed.
  - Connection outcomes, the 5.0-second init timeout, the language check, the 30-second request
    timeout and the two-retry policy are implemented and tested. Offering the Android text-to-speech
    settings screen is a UI action, deferred to task 17 with the rest of the import surface.
  - _Requirements: R8.12, R8.13, R8.18_
- [x] 10.6 Write voice engine tests
  - Add `android-app/app/src/test/java/com/audiochoice/mobile/narration/voice/SystemVoiceEngineTest.kt`
    with a fake `TextToSpeech` seam asserting no network access, fixed rate and pitch, exactly one
    timeline range per submitted unit, the two-retry policy, the 30-second timeout, and one timeline
    entry for a unit split across several requests.
  - _Requirements: R8.3, R8.11, R8.18, R8.19_

## 11. The render worker and its unique-work loop

- [x] 11. Render in the background, one chapter per job
- [x] 11.1 Implement `NarrationRenderWorker`
  - Create `android-app/app/src/main/java/com/audiochoice/mobile/narration/NarrationRenderWorker.kt`
    enqueued through
    `WorkManager.enqueueUniqueWork("audiochoice-narration-<sha256>", ExistingWorkPolicy.KEEP, …)`,
    running as a foreground worker via `setForeground` with a notification naming the book and the
    rendered and total chapter counts, removed within 5.0 seconds of rendering stopping. Render at
    most one chapter at a time per book.
  - _Requirements: R11.3, R11.10, R11.15_
- [x] 11.2 Implement the single-chapter loop and its triggers
  - Render one chapter, persist the outcome, re-evaluate `nextChapterToRender`, and either enqueue
    again or complete. Re-run the decision on plan persistence, on a chapter reaching rendered, on
    the playhead entering a later chapter, and on opening a book short of the window, each within
    5.0 seconds and without re-rendering an already rendered chapter.
  - _Requirements: R11.1, R11.5, R11.17, R11.18_
- [x] 11.3 Discard partial audio across cancellation and process death
  - Write to `chapter_<n>.m4a.partial` and rename on success. Delete any `.partial` found at worker
    start and reset that chapter to not rendered, so the guarantee holds across process termination
    and not only across clean cancellation.
  - _Requirements: R11.13_
- [x] 11.4 Implement failure handling and the all-failed state
  - Record render failed with a reason, report the failure naming the chapter title, offer to render
    that chapter again (returning it to not rendered), keep rendered audio playable, and step to the
    next required chapter within 5.0 seconds. When every chapter has failed, report that the book
    could not be narrated with the Selected_Voice, offer to render the plan again, offer to change
    the voice, and keep the plan and the Text_Scan_Events.
  - _Requirements: R11.14, R11.20_
- [x] 11.5 Present render progress
  - Publish rendered count, render-failed count, total count and the title of the chapter currently
    rendering, updated at least every 2.0 seconds.
  - _Requirements: R11.9_
- [x] 11.6 Implement listener pause and resume of rendering
  - Stop within 5.0 seconds on a pause request, keeping every rendered artifact, the Render_Queue
    and each Render_State, and start nothing further until resume is requested.
  - _Requirements: R11.16_
- [x] 11.7 Implement the Full_Book_Render_Request control
  - Add the detail-surface control that presents the chapter count, the storage estimate in
    megabytes, and — where the Selected_Voice is the Premium_Voice — that every remaining chapter
    will be synthesized through a Synthesis_Provider, recording the request only after confirmation.
    While in effect and not paused, render every chapter in plan order regardless of the window.
  - _Requirements: R11.6, R11.7, R11.8_
- [-] 11.8 Write render worker tests
  - Add `android-app/app/src/test/java/com/audiochoice/mobile/narration/NarrationRenderWorkerTest.kt`
    using WorkManager test helpers and a fake `VoiceEngine`: unique work rejects a second
    concurrent job for the same book, a `.partial` file left by a killed process is deleted and its
    chapter reset, the first chapter reaches rendered within 300 seconds for 20,000 characters with
    the system voice, and the book becomes playable from that chapter within 2.0 seconds with an
    indication that chapters remain.
  - _Requirements: R11.2, R11.13, R11.15, R11.19_
  - Skipped, with a finding. `NarrationRenderWorker` has no references outside its own file:
    nothing enqueues it and `NarrationRenderWork.coordinatorFactory`/`workFactory` are never set,
    so `doWork` would return success before rendering anything. Tests written against it would
    pass while asserting on dead code, which is worse than no tests because it implies background
    rendering works.
  - What actually renders is `NarrationViewModel.renderThenPlay`, calling
    `NarrationRenderCoordinator` directly. That is covered by `NarrationRenderCoordinatorTest`
    (unit) and `NarrationRenderPipelineTest` (instrumented), including the `.partial` sweep and
    playable-from-first-chapter behaviour this task lists.
  - Consequence for the listener: rendering stops when the app is backgrounded. Wiring the worker
    is a feature change to a working render path, not a test task, so it is left for a decision
    rather than done quietly here.

## 12. `PlaybackTimeline` and `PlayerViewModel` integration

- [x] 12. Put one translation layer between the controller and every position read
- [x] 12.1 Introduce `PlaybackTimeline` and route every read through it
  - Create `android-app/app/src/main/java/com/audiochoice/mobile/player/PlaybackTimeline.kt` with
    `PlaybackTimeline`, `DirectPlaybackTimeline` and `NarrationPlaybackTimeline`. In
    `com/audiochoice/mobile/player/PlayerViewModel.kt` change `trustedPositionMs` and
    `rawDurationMs` to go through the injected timeline, leaving the existing `liveTransport`
    protections untouched because they guard the controller rather than the arithmetic. Translating
    at each of the fourteen call sites individually would guarantee one is missed.
  - _Requirements: R12.2, R19.3_
- [x] 12.2 Write regression tests proving imported audiobooks do not change
  - Add `android-app/app/src/test/java/com/audiochoice/mobile/player/DirectPlaybackTimelineTest.kt`
    asserting `DirectPlaybackTimeline` returns exactly the controller's position and duration and
    issues exactly the controller's seek, and extend the existing `PlayerViewModel` tests to assert
    unchanged behaviour for a single-file book across seek, skip, chapter controls, sleep timer,
    progress checkpoints, bookmarks and filter skips.
  - _Requirements: R19.3_
- [x] 12.3 Branch `open()` on a playback source
  - Replace the single-URI resolution and its early return with
    `PlaybackSource.SingleFile`/`PlaybackSource.Narration`. For narration call
    `setMediaItems(items, startIndex, startPositionInItemMs)` from `timeline.locate(resumeBookTimeMs)`
    rather than seeking after `prepare()`, preserving the existing protection against a second
    `open()` adopting position 0. Replace `mediaItemFor` with `mediaItemsFor`, setting title, artist
    and artwork on every item and `mediaId = "<bookID>#<chapterIndex>"`, comparing only the book
    portion in the adoption check. A book with no rendered chapter loads no playlist, stays stopped,
    and reports that there is no rendered narration yet.
  - _Requirements: R12.1, R12.9, R12.14_
- [x] 12.4 Implement seeking, chapter and skip controls in Book_Time
  - Clamp a seek below 0 and at Narration_Duration, translate through `locate` to
    `controller.seekTo(index, offset)`, move by the configured interval in Book_Time, and drive
    chapter forward and back off the regenerated `AudioChapter` list. A seek past Narration_Duration
    or into an unrendered chapter positions at the end of the last rendered chapter, pauses, and
    reports that the position is not yet rendered.
  - _Requirements: R12.4, R12.5, R12.10_
- [x] 12.5 Add the filter-skip guard and a test that fails if it is removed
  - In `enforceEnabledFilters`, return early when `current.narration != null`, before the
    `scanEvents.isEmpty()` check, with a comment recording that a Narrated_Book's `ScanEvent`
    `startTime`/`endTime` are character offsets and that `FilterSkipPlanner` would read offset
    84,000 as 84,000 seconds and seek roughly 23 hours into the book (design tension 2). Add
    `android-app/app/src/test/java/com/audiochoice/mobile/player/NarrationFilterSkipGuardTest.kt`
    asserting a narrated book with events whose offsets exceed its duration in seconds issues no
    seek. This test exists to fail if the guard is ever removed.
  - _Requirements: R6.9_
- [x] 12.6 Add the completion guard and a test that fails if it is removed
  - Make `markFinishedIfAtEnd` return early unless every chapter is rendered. Without it a book with
    3 of 40 chapters rendered would be marked finished on reaching the end of chapter 3, synced as
    finished, and would have its stored playback speed cleared by `setFinished(true)` (design
    tension 3). Add a test that a partially rendered book playing to the end of its rendered audio
    is not marked finished and retains its recorded position, and that a fully rendered book still
    completes.
  - _Requirements: R12.11, R12.16_
- [x] 12.7 Wire speed, progress, bookmarks and the sleep timer to Book_Time
  - Keep the existing paths: per-book speed through `LocalAudioStore`, progress checkpoints at the
    existing cadence plus pause and stop, bookmarks at Book_Time positions, and the sleep timer
    pausing and saving progress.
  - _Requirements: R12.3, R12.6, R12.7, R12.8_
- [x] 12.8 Pause at the render frontier and auto-resume correctly
  - Reaching the end of the last rendered chapter while any chapter is unrendered pauses within
    1.0 second, keeps the position at that end, and reports that the next chapter is still
    rendering. When the next chapter finishes, extend the timeline within 2.0 seconds and resume
    from the kept position, guarded by a monotonically increasing `playbackIntentGeneration`
    incremented on every seek, pause and book open, because a boolean flag would race with a
    listener who paused during the render.
  - _Requirements: R8.10, R11.11, R11.12_
- [x] 12.9 Handle an unreadable Chapter_Audio
  - A `PlaybackException` on a chapter item pauses at that chapter's `bookStartMs`, retains the
    recorded position, and reports that the chapter must be rendered again.
  - _Requirements: R12.15_
- [x] 12.10 Republish duration and map taxonomy event times
  - Rebuild the timeline when a chapter enters or leaves rendered and republish duration within
    2.0 seconds on the existing polling tick. For a narrated book populate
    `PlaybackFilterEvent.startTime` by mapping the character offset through `readerTimeForCharacter`,
    leaving it null while the containing chapter is unrendered, so `PlaybackFilterTaxonomy` is
    unmodified and the displayed time is true.
  - _Requirements: R5.11, R12.2_
- [x] 12.11 Write narration playback tests
  - Add `android-app/app/src/test/java/com/audiochoice/mobile/player/NarrationPlaybackTest.kt`
    covering cross-chapter seek accuracy within 0.25 seconds, appending a chapter mid-playback
    without interrupting the current item, replacing an earlier item without moving the playhead,
    the empty-playlist case, and the auto-resume generation check.
  - _Requirements: R11.12, R12.1, R12.4, R12.14, R15.6_

## 13. Reader integration

- [x] 13. Follow the text with no change to the reader components
- [x] 13.1 Wire the reader to Book_Text and the Narration_Timeline
  - In `android-app/app/src/main/java/com/audiochoice/mobile/reader/`, render paragraphs from
    `ReaderParagraphParser.parse(Book_Text)` off the main thread, highlight the paragraph containing
    `readerCharacterForTime(narrationTimingRanges, bookTimeSeconds)` on the existing polling tick,
    seek on tap through `readerTimeForCharacter`, mask with
    `readerDisplayParagraphs(paragraphs, filteredRanges.map { ReaderMask(it.start, it.end) })`, and
    keep the existing reader position path and device-wide `ReaderSettings` unchanged.
  - _Requirements: R13.1, R13.2, R13.3, R13.4, R13.6, R13.7_
- [x] 13.2 Handle the reader edge cases
  - Render non-prose text with no highlight; leave the position unchanged and report that the tapped
    text has no narration yet when a tapped paragraph has no covered offset; keep the last highlight
    and scroll not at all when `readerCharacterForTime` returns null; bring a changed highlight fully
    into view within 500 ms.
  - _Requirements: R13.8, R13.9, R13.10, R13.11_
- [x] 13.3 Write reader coverage tests
  - Add `android-app/app/src/test/java/com/audiochoice/mobile/reader/NarrationReaderSyncTest.kt`
    with a property test that a rendered chapter holds exactly one `ReaderTimingRange` per unit whose
    range no Filtered_Range covers in full, and example tests for a fully masked paragraph rendering
    not at all and for a highlight never landing on a non-prose block.
  - _Requirements: R13.4, R13.5, R13.8_

## 14. Backend schema

- [x] 14. Add the one additive, forward-only migration
- [x] 14.1 Write `027_epub_narration.sql`
  - Create `backend/Database/Migrations/027_epub_narration.sql` creating `narration_text_scans`,
    `narration_text_scan_events` (with the `start_character`/`end_character` check and the scan
    index), `narration_chapter_renders`, `narration_voice_acknowledgements` and
    `narration_measurements`, and adding `position_unit varchar(20) not null default 'seconds'` to
    `filter_reports`. No Book_Text column anywhere. Confirm `PostgresDatabaseInitializer` applies it
    in filename order and add an initializer test asserting the migration is picked up and that no
    existing table changes shape.
  - _Requirements: R5.6, R7.6, R9.9, R10.7, R10.12, R10.13, R10.15, R17.5, R18.6, R18.7, R19.7_

## 15. Backend text scan

- [x] 15. Scan Book_Text without storing a character of it
- [x] 15.1 Declare the narration wire contracts
  - Create `android-contract/src/main/kotlin/com/audiochoice/contracts/NarrationContracts.kt` and
    `backend/AudioChoice.Api/Contracts/NarrationContracts.cs`, reusing `BookFingerprint` and
    `ScanEvent` from `CloudContracts` rather than declaring narration-specific copies. Confirm
    `android-contract` is already on the Android main source set in
    `android-app/app/build.gradle.kts`.
  - _Requirements: R5.2, R5.3_
- [x] 15.2 Implement `TextScanPipeline`
  - Create `backend/AudioChoice.Api/Processing/TextScanPipeline.cs` branching above
    `ScanPipeline.Process`: segment Book_Text into paragraph-scale passages matched to
    `MaximumSegmentsPerAnalysisRequest`, hand them to the unmodified `IContentAnalysisProvider` as
    `TranscriptSegment` values whose `StartTime`/`EndTime` carry character offsets, and return
    `ScanEvent` values in that same space. Apply `DeterministicContentDetector.DetectProfanity`,
    because a literal word match is exact in either space and the same `aggregateKey` is required.
    Do not apply `SceneEventPostProcessor`: its 45-second merge gap, 8-second padding and 30-second
    minimum are meaningless as character counts (design tension 6), so complete-scene events are
    returned as the analysis provider produced them.
  - _Requirements: R5.2, R5.3, R5.7_
- [x] 15.3 Add `POST /v1/narration/text-scans`
  - Add the endpoint in `backend/AudioChoice.Api/Program.cs` behind the narration options flag: bind
    Book_Text to a local variable, enforce a 120-second budget with a `CancellationTokenSource`
    returning 504 on expiry, return 400 for empty or oversized `bookText` mirroring the existing
    8,000,000-character reader-alignment bound, and override the request record's `ToString` to omit
    the text so no log scope can print a whole novel.
  - _Requirements: R5.1, R5.3, R5.4, R5.5_
- [x] 15.4 Persist events only, and keep narrated books out of Explore
  - Write Text_Scan_Events, scan date, scanner version, taxonomy version and fingerprint to
    `narration_text_scans`/`narration_text_scan_events` through a new
    `backend/AudioChoice.Api/Services/PostgresNarrationTextScanStore.cs`. Create no `scan_results`
    row and no `ScanCatalog` entry, so Explore exclusion is structural rather than a filter that
    could be forgotten.
  - _Requirements: R5.6, R18.7_
- [x] 15.5 Write the non-persistence and purpose-limitation tests
  - Add tests under `backend/AudioChoice.Api.ContractTests/` embedding a marker string in Book_Text
    and asserting it appears in no file under `AudioChoiceDataPaths.Root`, no log sink, and no
    Postgres table after a scan; asserting the response body carries no Book_Text; asserting
    `TextScanPipeline` has exactly one outbound processor dependency; and asserting the request
    record's `ToString` omits the text.
  - _Requirements: R5.4, R5.5, R5.6, R5.7_
- [x] 15.6 Write the Explore exclusion test
  - Assert a completed text scan produces no publishable catalogue entry and that the existing audio
    scan path is unaffected.
  - _Requirements: R18.7, R19.7_

## 16. Client text scan integration

- [x] 16. Acknowledge, scan, validate, and filter offline afterwards
- [x] 16.1 Gate Book_Text on the Text_Scan_Acknowledgement
  - Add the first-use statement surface naming each category of processor — the AudioChoice backend,
    the third-party model provider that classifies content, and, where the Premium_Voice is
    selected, the AudioChoice-owned Synthesis_Endpoint on Amazon SageMaker and Amazon Polly as the
    fallback — and record the version, text and timestamp. Request no scan and send no Book_Text
    while no acknowledgement is recorded, keeping the book unrendered.
  - _Requirements: R5.8, R5.9, R5.16, R5.17_
- [x] 16.2 Request and persist the scan, then work offline
  - Add `android-app/app/src/main/java/com/audiochoice/mobile/narration/TextScanClient.kt` posting
    Book_Text once per book before any unit is submitted, persisting events, scanner version and
    scan date to `text-scan.json`, and requesting no further scan while the recorded scanner version
    equals the current one.
  - _Requirements: R5.1, R5.15_
- [x] 16.3 Validate every returned offset as a batch
  - Discard the whole response and treat the scan as not completed when any event has
    `start >= end` or `end > Book_Text.length`, because an out-of-range offset means client and
    server disagree about the coordinate space and no event in the batch can be trusted.
  - _Requirements: R5.18_
- [x] 16.4 Implement scan retry and the continue-without-results state
  - Retry three times with increasing delay, then report that filter results are unavailable, offer
    a retry, and keep the book unrendered until a scan completes or the listener continues without
    results. Record and present that state in the library list and on the detail surface until a
    scan completes.
  - _Requirements: R5.13, R5.14_
- [x] 16.5 Wire the filter controls through the existing stack
  - Determine enabled events with `PlaybackFilterPredicate.isEnabled`, present controls through
    `PlaybackFilterTaxonomy`, and read and write choices through `BookFilterSettings`, all
    unmodified, so a narrated book presents the same control tree and syncs by the same path.
  - _Requirements: R5.10, R5.11, R5.12_
- [x] 16.6 Write client scan tests
  - Add `android-app/app/src/test/java/com/audiochoice/mobile/narration/TextScanClientTest.kt`
    covering the acknowledgement gate blocking any request, batch invalidation on a bad offset, the
    three-retry path, offline reuse of stored events, and identical taxonomy control trees for a
    narrated and an imported book with the same events.
  - _Requirements: R5.10, R5.11, R5.13, R5.16, R5.18_

## 17. Import orchestration and library identity

- [x] 17. Turn an accepted EPUB into an ordinary library book
- [x] 17.1 Implement the narration import flow
  - Add `android-app/app/src/main/java/com/audiochoice/mobile/narration/NarrationImporter.kt` and the
    library import action, offering `application/epub+zip` and `.epub` by name and reusing the
    existing permissive MIME array from `AudioChoiceApp` with a `.epub` suffix filter for providers
    reporting a generic type. Off the main thread: take the persistable read permission before
    reading Book_Text, stream SHA-256 and the byte count in one pass within 30.0 seconds for 50 MB,
    validate, build `BookFingerprint(version = 1, sha256, fileSize, duration = null, fileType =
    "epub")`, then `PUT /v1/library`, then scan, then plan, then render.
  - _Requirements: R1.1, R1.2, R1.3, R18.6_
- [x] 17.2 Implement metadata, title fallback and cover storage
  - Record the first title and author in document order truncated to 500 characters; derive the
    title from the filename without `.epub`, or the first 8 characters of the SHA-256, recording
    that it was derived; record an absent author and complete the import; store the manifest cover
    through `LocalAudioStore.saveBookCover`, storing nothing and leaving the default cover when it
    is absent or undecodable.
  - _Requirements: R1.4, R1.5, R1.6, R1.11, R1.12_
- [x] 17.3 Implement duplicate handling and permission failure
  - A matching SHA-256 opens the existing book, replaces the persisted content URI, keeps the plan,
    audio and position, creates no second entry, and reports that the book is already in the
    library. A failure to take the persistable permission creates no book, persists no plan, and
    reports that the file could not be opened for reading.
  - _Requirements: R1.8, R1.10_
- [x] 17.4 Keep EPUB attachment to an imported audiobook unchanged
  - Leave `PlayerViewModel.attachEpub` behaviour as is and create no Narrated_Book from it; assert
    the two entry points stay distinct.
  - _Requirements: R1.7_
- [x] 17.5 Write import tests
  - Add `android-app/app/src/test/java/com/audiochoice/mobile/narration/NarrationImporterTest.kt`
    covering the fingerprint fields and the 30-second bound for 50 MB, the title and author
    fallbacks, the duplicate path leaving artifacts intact, the permission-failure path, and a
    `PUT /v1/library` body carrying `fileType = "epub"` with no duration.
  - _Requirements: R1.3, R1.5, R1.8, R1.10, R1.11, R1.12, R18.6_

## 18. Narration tier resolution

- [x] 18. Derive the tier from the account, never from the device
- [x] 18.1 Implement `NarrationTierStore`
  - Create `android-app/app/src/main/java/com/audiochoice/mobile/narration/NarrationTierStore.kt`
    deriving Premium_Tier only while `GET /v1/account/access` reports `isActive` and an `expiresAt`
    that is absent or future, from Account_Access alone and no local purchase state. Read at least
    every 24 hours while the library holds a Narrated_Book and on opening a voice selection surface,
    recording the derived tier, the `plan` value and the read timestamp on each success.
  - _Requirements: R7.1, R7.2, R7.3, R7.10_
- [x] 18.2 Implement the grace period and voice availability
  - Keep the last recorded tier for 7 days from the most recent successful read; past that treat the
    tier as Free and report that the entitlement could not be confirmed. Offer System and
    Local_Neural voices in Free_Tier with no Spoken_Text submitted to the backend, and additionally
    the Premium_Voice in Premium_Tier. Present no purchase control and no price. Google Play Billing
    is out of scope; premium entitlements come from the existing
    `POST /v1/admin/accounts/{userID}/entitlements` during the experimental cycle.
  - _Requirements: R7.4, R7.5, R7.8, R7.9, R7.11, R7.12_
- [x] 18.3 Implement the Premium-to-Free transition
  - Keep every premium-rendered Chapter_Audio playable, submit no further unit to the Premium_Voice,
    report that the premium voice is no longer available, and offer to render the remaining chapters
    with an on-device voice; accepting records that voice as Selected_Voice, keeps the premium audio,
    and renders the rest with the chosen voice. This is why provider and voice are recorded per
    chapter in `narration_chapter_renders` rather than per book.
  - _Requirements: R7.6, R7.7_
- [x] 18.4 Write tier tests
  - Add `android-app/app/src/test/java/com/audiochoice/mobile/narration/NarrationTierStoreTest.kt`
    as a state-machine test over every transition, including the grace boundary at exactly 7 days,
    an unreadable response from both tiers, an `expiresAt` in the past, and the mixed-provider book
    after a downgrade.
  - _Requirements: R7.1, R7.6, R7.8, R7.9_

## 19. Storage accounting and eviction

- [x] 19. Keep narration audio inside the storage the device has
- [x] 19.1 Implement the pre-render storage estimate and the reserve check
  - Add `android-app/app/src/main/java/com/audiochoice/mobile/narration/NarrationStorage.kt`
    estimating megabytes from the Render_Queue's total Spoken_Text character count via a per-engine
    characters-per-second constant and the encoder bitrate. When the estimate exceeds free space
    less the 1.0 GB Storage_Reserve, report the shortfall in megabytes, keep every chapter not
    rendered, and keep the plan and Text_Scan_Events.
  - _Requirements: R16.1, R16.2_
- [x] 19.2 Measure free space during rendering and stop at the reserve
  - Measure before each chapter and at least every 30.0 seconds during one; at or below the reserve
    stop within 5.0 seconds, discard the partial audio, keep rendered audio and the queue, and
    report that the device is low on storage.
  - _Requirements: R16.3, R16.12_
- [x] 19.3 Present per-book storage and the discard-all control
  - Present the total byte count of a book's Chapter_Audio in megabytes, updated within 5.0 seconds
    of a write or delete. Add a discard-all control presenting the reclaimable megabytes and the
    count needing re-render, discarding nothing until confirmed, and keeping the plan, timelines,
    Text_Scan_Events, Pronunciation_Rules and position.
  - _Requirements: R16.5, R16.6, R16.13_
- [x] 19.4 Implement eviction, disabled by default
  - When enabled for a book, on playback passing a chapter's last offset delete the Chapter_Audio of
    every chapter ending more than 2 chapters before the playhead's chapter and holding no bookmark,
    mapping a bookmark's Book_Time to a chapter through `NarrationTimeline.locate`. Deleting a
    Chapter_Audio sets the chapter to not rendered and keeps its Chapter_Timeline.
  - _Requirements: R16.8, R16.9, R16.10_
- [x] 19.5 Implement book deletion and partial-failure reporting
  - Hook the existing `LocalAudioStore.remove` path that `LibraryViewModel.delete` calls to delete
    the whole `narration/<sha256>/` directory and the book-scoped rules, keep account-scoped rules,
    and release the persisted read permission. On a failed deletion, delete the rest, keep the book
    absent from the library, and report that some narration data could not be removed.
  - _Requirements: R16.7, R16.14_
- [x] 19.6 Write storage tests
  - Add `android-app/app/src/test/java/com/audiochoice/mobile/narration/NarrationStorageTest.kt`
    asserting the estimate falls within 30 percent of actual for a fully rendered fixture book, the
    reserve check refuses to start, the reserve stop discards only the partial file, eviction skips a
    bookmarked chapter, and that `purgeOrphanedAudioFiles` reclaims no narration audio because
    `narration/` is deliberately outside `PURGEABLE_AUDIO_DIRECTORIES`.
  - _Requirements: R16.1, R16.2, R16.8, R16.11, R16.12_

## 20. Library presentation

- [x] 20. Tell narrated books apart without a second library
- [x] 20.1 Present narration state on list rows and the detail surface
  - In `android-app/app/src/main/java/com/audiochoice/mobile/library/`, add a synthesized-narration
    indication on every narrated row and the detail surface and on no imported audiobook; the
    rendered and total chapter counts while any chapter is unrendered, updated within 2.0 seconds of
    a chapter entering rendered; the Selected_Voice name and kind; a render-failed indication with
    the failed count; and an unavailable-voice indication with the voice selection control when the
    recorded voice identifier matches no available voice.
  - _Requirements: R18.1, R18.2, R18.3, R18.9, R18.10_
- [x] 20.2 Present duration across two library tabs
  - Present Narration_Duration in hours and minutes for a fully rendered book, the rendered duration
    with an indication that it covers rendered chapters only otherwise. Present the library as two
    tabs -- Audiobooks and Ebooks -- sharing one import action that routes a file to the correct tab
    by what it is, sharing the same sort keys and filter controls, and ordering a book with no
    duration after every book with one. An imported audiobook with an attached EPUB appears in the
    audiobook tab only. See the decision note on R18.5.
  - _Requirements: R18.4, R18.5, R18.8_
- [x] 20.3 Write library presentation tests
  - Add `android-app/app/src/test/java/com/audiochoice/mobile/library/NarrationLibraryTest.kt`
    covering the indication appearing only for narrated books, count updates within 2.0 seconds,
    duration ordering with an absent duration, and the partially-rendered duration caveat.
  - _Requirements: R18.1, R18.2, R18.5, R18.8_

## 21. Filter reports for narrated books

- [x] 21. Report a narrated moment in the coordinate space triage can read
- [x] 21.1 Add the additive `positionUnit` field on both sides
  - Add `val positionUnit: String? = null` to `FilterReportRequest` in
    `android-contract/src/main/kotlin/com/audiochoice/contracts/CloudContracts.kt` and the matching
    optional property in `backend/AudioChoice.Api/Contracts/FilterReportContracts.cs`, persisting it
    through `PostgresFilterReportStore` into the defaulted `position_unit` column. `positionSeconds`
    carries a character offset for a narrated book (design tension 7); the field is optional with a
    default so an existing client's request body is byte-identical and no endpoint shape changes.
  - _Requirements: R17.5, R19.7_
- [x] 21.2 Add narration report composition
  - Extend `FilterReportComposer` with narration variants setting `positionUnit =
    "characterOffset"`, taking the offset from `readerCharacterForTime` for the reported Book_Time,
    expressing the look-back as a character window rather than a time window, and attaching the
    `scanEventID` and `categoryID` of the containing Enabled_Text_Scan_Event, choosing the lowest
    start offset when several contain the offset. Include no Book_Text, Spoken_Text or narration
    audio.
  - _Requirements: R17.1, R17.2, R17.3, R17.4_
- [x] 21.3 Implement the no-mapping cases and queueing
  - Send nothing and report that the position maps to no position in the book text when no
    `ReaderTimingRange` covers the reported Book_Time; send nothing, report that no filtered passage
    covers the position and leave filter choices unchanged when no enabled event contains the
    offset; queue through the existing `FilterReportQueue` with exactly-once delivery, narrowing the
    cap to 100 narration reports and discarding oldest first; acknowledge acceptance within
    2.0 seconds.
  - _Requirements: R17.6, R17.7, R17.8, R17.9_
- [x] 21.4 Write filter report tests
  - Add tests asserting a beta-shaped request body serialises byte-identically without
    `positionUnit`, that a narration report carries `positionUnit = "characterOffset"` and the
    correct event identifiers, the lowest-start tie-break, both no-mapping paths, and the 100-report
    cap.
  - _Requirements: R17.2, R17.3, R17.5, R17.6, R17.7, R17.8, R19.7_

## 22. Pronunciation rules

- [x] 22. Fix a mispronunciation once, in one pass, without touching Book_Text
- [x] 22.1 Implement rule storage and scoping
  - Add `android-app/app/src/main/java/com/audiochoice/mobile/narration/PronunciationRules.kt`
    persisting book-scoped rules against the Source_EPUB SHA-256 with their recording order and
    account-scoped rules against the account, applying account rules to every narrated book, and
    leaving the plan and every unit Source_Range unchanged when a rule is recorded, edited or
    deleted.
  - _Requirements: R14.1, R14.4, R14.5_
- [x] 22.2 Implement matching and single-pass application
  - Apply rules to the characters remaining after Filtered_Range exclusion and before submission,
    never to Book_Text, and never across the boundary of an excluded Filtered_Range. Match
    case-insensitively with non-alphanumeric boundaries. Apply in one left-to-right pass in
    ascending offset order, at most one rule per character, never over characters already
    substituted, with book scope ahead of account scope and earlier-recorded ahead of later within a
    scope. A naive sequence of independent replacements would let one rule rewrite another's output.
  - _Requirements: R14.2, R14.3, R14.7, R14.8_
- [x] 22.3 Implement validation, the re-render offer and the preview
  - Reject an empty or over-long form naming which form is out of bounds and retaining the entered
    values; reject a case-insensitive duplicate within a scope and offer to edit the existing rule;
    reject at 200 rules per scope leaving every persisted rule unchanged. On record, edit or delete,
    present the count of rendered chapters holding at least one match under the same matching rule,
    offer to render them again, and discard no audio until accepted. Speak the replacement form
    through the same `VoiceEngine` used for rendering, beginning within 3.0 seconds and speaking no
    longer than 10 seconds.
  - Decision: the preview speaks through the phone's own voice rather than whichever voice is
    selected. The premium provider bills per character and exposes no per-utterance path, so
    previewing a syllable through it would spend the listener's allowance and send a fragment of
    their book's text off the device for a result the free voice already gives. The free voice is
    also the default most previews are judged against.
  - _Requirements: R14.6, R14.9, R14.10, R14.11, R14.12_
- [x] 22.4 Write pronunciation rule tests
  - Add `android-app/app/src/test/java/com/audiochoice/mobile/narration/PronunciationRulesTest.kt`
    with a property test that no rule applies to characters another rule substituted and at most one
    rule applies per character, plus example tests for the boundary rule, the precedence order, the
    "Rhysand"/"and" cascade case, no match across a removal boundary, and each validation branch.
  - _Requirements: R14.2, R14.7, R14.8, R14.10, R14.11, R14.12_

## 23. Re-render on a filter change

- [x] 23. Change a filter mid-book without silently leaving audio inconsistent
- [x] 23.1 Identify the affected chapters
  - Add `android-app/app/src/main/java/com/audiochoice/mobile/narration/FilterChangeCoordinator.kt`
    recomputing enabled state per event with `PlaybackFilterPredicate.isEnabled` and identifying,
    within 2.0 seconds, every rendered or rendering chapter overlapping an event whose enabled state
    changed. When nothing is identified, write the choice through `BookFilterSettings` with no
    confirmation and discard nothing.
  - _Requirements: R15.1, R15.9_
- [x] 23.2 Present the confirmation and honour a decline
  - Present the chapter count, a whole-minute re-render estimate derived from their unit character
    count, and — where the Selected_Voice is the Premium_Voice — the count that will be synthesized
    again. Write no changed choice and discard no audio until confirmed. On decline restore the
    previous choice through `BookFilterSettings` and leave every audio file, timeline and
    Render_State unchanged.
  - _Requirements: R15.2, R15.4, R15.7_
- [x] 23.3 Requeue, prioritise and restore the position on confirmation
  - Record the character offset from `readerCharacterForTime` at confirmation, because re-rendering
    changes chapter durations and the Book_Time no longer denotes the same words. Stop any
    identified chapter mid-render within 5.0 seconds discarding its partial audio, discard the
    identified Chapter_Audio and Chapter_Timelines, set them not rendered, requeue in plan order,
    render the chapter at the position first while pausing and reporting that it is being rendered
    again, and restore the position through `readerTimeForCharacter` once every identified chapter
    before that offset is rendered. Where the position lies in an unidentified chapter, continue
    playing uninterrupted by replacing the affected playlist item rather than resetting the playlist.
  - _Requirements: R15.3, R15.5, R15.6, R15.8, R15.10, R15.11_
- [x] 23.4 Write filter change tests
  - Add
    `android-app/app/src/test/java/com/audiochoice/mobile/narration/FilterChangeCoordinatorTest.kt`
    covering identification within 2.0 seconds, the no-op path writing without confirmation, the
    decline path leaving everything untouched, playback continuing while an unrelated chapter
    re-renders, and position restoration landing on the same characters after durations changed.
  - _Requirements: R15.1, R15.4, R15.6, R15.8, R15.9_

## 24. Backend synthesis: the provider seam, Polly, and fallback routing

- [x] 24. Launch premium synthesis on Polly, behind a router that can fall back
- [x] 24.1 Declare the `ISynthesisProvider` seam
  - Create `backend/AudioChoice.Api/Processing/SynthesisProvider.cs` with `SpokenUnit`,
    `UnitTiming`, `ChapterSynthesisInput`, `SynthesizedChapter` and `ISynthesisProvider`, mirroring
    the shape and registration of `ITranscriptionProvider`, plus
    `backend/AudioChoice.Api/Processing/NarrationOptions.cs` for the provider selection setting.
  - _Requirements: R10.1_
- [x] 24.2 Implement `PollySynthesisProvider` as the launch implementation
  - Create `backend/AudioChoice.Api/Processing/PollySynthesisProvider.cs` synthesizing one request
    per Narration_Unit for exact per-unit durations without speech-mark parsing, concatenating with
    `ffmpeg` through the existing `IProcessRunner` abstraction that `FfmpegAudioChunker` uses, and
    encoding once to single-channel Opus at 32 kbps. Register with `Provider = "polly"` as the
    provider in effect, because Polly Generative needs no AudioChoice-operated infrastructure.
  - _Requirements: R10.3, R10.10_
- [x] 24.3 Implement `SynthesisRouter` and its routing rules
  - Create `backend/AudioChoice.Api/Processing/SynthesisRouter.cs` owning the primary and the
    fallback: route to the fallback on a primary error, an endpoint-unavailable report, or no audio
    within 60 seconds for one chapter; route to the fallback when a scaled-to-zero endpoint returns
    no audio within the recorded Cold_Start_Delay plus 60 seconds, reading that delay from
    `narration_measurements`; and configure the fallback as the provider in effect when billing
    coverage is unverified. Hold no reference to `ITranscriptionProvider`, and add a startup
    assertion that fails the process if `AudioChoice:OpenAI:FasterWhisperEndpoint` and
    `AudioChoice:Narration:SageMakerEndpoint` resolve to the same host, so no narration synthesis
    can run on the Transcription_GPU_Host.
  - _Requirements: R10.1, R10.4, R10.5, R10.6, R10.18_
- [-] 24.4 Narration_Object_Store and SAS download URLs — NOT BEING BUILT

  > **Replaced 2026-08-29.** Chapter audio is returned in the poll response body instead. Audio
  > derived from a listener's book sitting in a storage container is the same disclosure the text
  > handling is careful to avoid, and unlike the text it would sit there indefinitely under a
  > retention policy rather than under the absence of anywhere to put it. There is no container to
  > leak, no signed URL to mis-scope and no expiry to get wrong, because there is nowhere the audio
  > is. A chapter is a few megabytes, which is an ordinary download. The cost is that an
  > uncollected job's result is lost on restart, which costs one re-synthesis; the client already
  > retries. This also removes a storage account, a container and a SAS-signing path from the
  > design.
  - Create `backend/AudioChoice.Api/Services/BlobNarrationObjectStore.cs` writing each Chapter_Audio
    to a new `narration-audio` container on the existing storage account, following the
    user-delegation SAS pattern in `BlobCompanionTransferStorage` to issue a URL for the requesting
    account only, expiring within 3600 seconds. Serve audio from the object store rather than from
    any synthesis host.
  - _Requirements: R10.8, R10.9_
- [x] 24.5 Add the narration chapter, voice and acknowledgement endpoints
  - Add to `Program.cs`: `POST /v1/narration/chapters` returning 202 with a `jobID`, verifying
    Premium_Tier and a current acknowledgement and returning 403 otherwise; `GET
    /v1/narration/chapters/{jobID}` returning status, provider, model version, duration, the SAS
    download URL, its expiry and the unit timings; `GET /v1/narration/voices` returning voices with
    pre-rendered fixed sample assets plus the agreement version and text; and `POST
    /v1/narration/acknowledgements`, idempotent on `(userID, agreementVersion)`.
  - Chapter synthesis is a job rather than a synchronous response because a chapter can hold 20,000
    characters; R9.7's 30 seconds bounds each HTTP interaction while R10.5's 60 seconds bounds the
    provider call inside the router (design tension 4).
  - _Requirements: R9.6, R9.9, R10.7, R10.8, R10.9_
- [x] 24.6 Record renders and discard Spoken_Text
  - Persist provider, model version, voice, duration and object path per chapter to
    `narration_chapter_renders` through a new
    `backend/AudioChoice.Api/Services/PostgresNarrationRenderStore.cs`, persist acknowledgements to
    `narration_voice_acknowledgements`, and write no character of Spoken_Text to any persistent
    store, log, cache, checkpoint or telemetry record, retaining it only until the Chapter_Audio is
    written. Override the synthesis request record's `ToString` to omit the text.
  - _Requirements: R9.9, R10.7, R10.11_
- [x] 24.7 Write backend synthesis tests
  - Add tests under `backend/AudioChoice.Api.ContractTests/` with a fake `ISynthesisProvider` pair
    covering fallback on primary error, on primary timeout, on the cold-start budget being exceeded
    and on billing coverage being unverified; a marker-string test that no Spoken_Text survives in
    any file, log or table; a 403 for a non-premium account and for a stale agreement version; a SAS
    URL scoped to one account and expiring within 3600 seconds; Opus mono 32 kbps output; and the
    startup assertion failing when the two endpoint settings collide.
  - _Requirements: R10.4, R10.5, R10.6, R10.9, R10.10, R10.11, R10.18_

## 25. The premium voice client path

- [x] 25. Send text off the device only after saying so
- [x] 25.1 Implement the acknowledgement flow
  - Add `android-app/app/src/main/java/com/audiochoice/mobile/narration/voice/PremiumVoiceAgreement.kt`
    presenting, before recording anything, that the book's Spoken_Text is sent to the AudioChoice
    backend for synthesis, that synthesis runs on the AudioChoice-owned Synthesis_Endpoint on Amazon
    SageMaker or on Amazon Polly as the named fallback, that Spoken_Text is retained only until that
    request's Chapter_Audio is written, and that the System and Local_Neural voices remain available
    and send no text off the device, with accept and decline controls. Record the version, text and
    timestamp account-scoped before any submission; keep the Premium_Voice unselected until a
    current-version acknowledgement exists; on decline or dismissal change nothing at all.
  - _Requirements: R9.1, R9.2, R9.3, R9.4, R9.11_
- [x] 25.2 Implement offline acknowledgement and version changes
  - Record locally when the backend is unreachable, treat that record as sufficient to submit,
    retain it until the backend confirms persistence, and deliver it on the backend's next response.
    On an agreement version change re-present the statement and submit nothing further until the new
    version is acknowledged, leaving rendered audio playable.
  - _Requirements: R9.10, R9.12_
- [x] 25.3 Implement `PremiumVoiceEngine`
  - Add `.../narration/voice/PremiumVoiceEngine.kt` posting a chapter's units — with filtered
    characters already removed — submitting, polling, downloading the Chapter_Audio into app-private
    storage and treating it as that chapter's Chapter_Audio for every purpose. Include no character
    of a Filtered_Range in any request.
  - _Requirements: R6.3, R10.19_
- [x] 25.4 Implement premium retry and connectivity handling
  - On failure or no completion within 30.0 seconds resubmit at most three times waiting 2.0, 4.0
    and 8.0 seconds, then record the chapter as render failed. Treat lost connectivity as
    `Cancelled` rather than `Failed`: pause the queue within 5.0 seconds, consume no attempt while
    connectivity is absent, report that rendering continues when it returns, leave rendered audio
    playable, and resume within 10.0 seconds of its return.
  - _Requirements: R9.7, R9.8_
- [x] 25.5 Present voice presentation and samples
  - Present the Premium_Voice as included in the Premium_Tier with no per-book charge and no
    character count, and play a 3.0-to-30.0-second sample from `GET /v1/narration/voices` before
    selection, submitting no Spoken_Text of the book to produce it. Changing the Selected_Voice
    presents the rendered chapter count and requires confirmation; confirming discards every
    Chapter_Audio and Chapter_Timeline and resets every Render_State; declining keeps everything.
  - _Requirements: R8.15, R8.16, R8.17, R9.5, R9.6_
- [x] 25.6 Write premium client tests
  - Add `android-app/app/src/test/java/com/audiochoice/mobile/narration/voice/PremiumVoiceEngineTest.kt`
    asserting no submission without a current acknowledgement, the offline record permitting
    submission and being delivered once, the stale-version block, the 2/4/8 retry schedule, that a
    connectivity drop consumes no attempt, that no filtered character appears in a request body, and
    that a voice change discards audio only after confirmation.
  - _Requirements: R6.3, R8.15, R8.16, R8.17, R9.4, R9.7, R9.8, R9.10, R9.12_

## 26. The local neural voice and its rate gate — DEFERRED ON EVIDENCE

> **Deferred 2026-08-30, on the strength of the measurement group 26.2 was written to take.**
>
> The premise was that the device's built-in voice is mediocre, so a bundled neural model would be
> a meaningful free-tier upgrade, gated on whether the device could keep up. The measurement says
> the premise no longer holds on current hardware: a Samsung SM-S936U on Android 16 reports **67
> voices available** and synthesizes at **28.2x real time**, and Google's own on-device voices are
> already neural. The system voice speaks at 18.4 characters a second against Polly's 18.0 — the
> two are within two percent of each other.
>
> So the cost is a 100 MB application asset, a download-and-confirm flow, a model-version migration
> path and a second synthesis engine to maintain; and the measured benefit over what the phone
> already has is unquantified and plausibly zero. The rate gate itself (26.2, 26.3) was built and
> is worth keeping: it now protects the *system* voice on slow devices, which is a real case that
> has not been measured.
>
> What would change this: a measurement on a low-end or older device showing the system voice below
> the 3x floor, or showing it markedly worse in quality. Neither has been taken. The `VoiceEngine`
> seam means adding an engine later is additive, not a rewrite.

- [-] 26. Offer a better on-device voice only where the device measurably keeps up
- [-] 26.1 Implement `LocalNeuralVoiceEngine`
  - Add `android-app/app/src/main/java/com/audiochoice/mobile/narration/voice/LocalNeuralVoiceEngine.kt`
    running a neural model held as an application asset of 100 MB or less, making no network request
    during synthesis and sending no character off the device, behind the same `VoiceEngine` seam.
    Present the model's download size in megabytes and download only after confirmation when the
    model is absent.
  - _Requirements: R8.4, R8.5_
- [x] 26.2 Implement the on-device Synthesis_Rate measurement
  - Measure by synthesizing a fixed 200-to-400-character measurement text and dividing produced
    audio duration by wall-clock time, recording the result and the model version in the device-wide
    `neural_voice_rate` and `neural_voice_model_version` keys, and re-measuring when the model
    version changes.
  - _Requirements: R8.6, R8.9_
- [x] 26.3 Gate availability on the measured rate
  - Offer the voice only where the measured rate exceeds Playback_Speed_Ceiling 2.0 times
    Synthesis_Rate_Margin 1.5, that is 3.0, derived from the two named constants rather than
    hard-coded as a tunable. At or below the threshold present it as unavailable on that device,
    state that the device cannot render fast enough to keep up with listening, and keep the
    System_Voice selected. This task must not offer the voice until task 26.4 has recorded a
    measurement on a Mid_Range_Device.
  - _Requirements: R8.7, R8.8_
- [x] 26.4 Write the Mid_Range_Device rate benchmark harness
  - Add an instrumented benchmark under `android-app/app/src/androidTest/` that runs the measurement
    on a device carrying 6 GB RAM and 8 CPU cores whose system-on-chip was released 3 to 5 years
    before the run, and writes a Narration_Measurement_Record of kind
    `local_neural_synthesis_rate` carrying the value, date, device model and software version.
    Record the result; do not substitute an assumed value.
  - _Requirements: R8.6, R10.14_
- [-] 26.5 Implement neural voice failure fallback
  - On a model that cannot be loaded, or three consecutive unit errors, record the System_Voice as
    Selected_Voice, report that the neural voice is unavailable and narration continues with the
    system voice, and keep every rendered Chapter_Audio playable.
  - _Requirements: R8.14_
- [-] 26.6 Write local neural voice tests
  - Add `android-app/app/src/test/java/com/audiochoice/mobile/narration/voice/LocalNeuralVoiceEngineTest.kt`
    asserting no network access during synthesis, the threshold gate at exactly 3.0 and just above
    and below, re-measurement on a model version change, the download confirmation gate, and the
    three-consecutive-error fallback keeping rendered audio playable.
  - _Requirements: R8.4, R8.5, R8.7, R8.8, R8.9, R8.14_

## 27. Measurements and the Render_Ahead_Window

- [x] 27. Fix the derived values from measurements rather than from guesses
- [x] 27.1 Write the premium Synthesis_Rate benchmark harness
  - Add a benchmark under `backend/AudioChoice.Api.ContractTests/` (or a small harness project)
    that renders a full Reference_Chapter of 15,000 to 25,000 characters end to end from request
    submission to the last audio sample, including concatenation and encoding, and writes a
    Narration_Measurement_Record of kind `premium_synthesis_rate` with the value, date, target and
    software version. Run it from the Azure Container App that will make the call in production, not
    from a workstation, so the cross-cloud hop is inside the number. Record the result; do not
    substitute an assumed value.
  - _Requirements: R10.12_
- [x] 27.2 Add the measurement record store
  - Add `backend/AudioChoice.Api/Services/PostgresNarrationMeasurementStore.cs` reading and writing
    `narration_measurements`, and expose the Cold_Start_Delay and the Render_Ahead_Window value in
    effect to `SynthesisRouter` and to the client configuration.
  - _Requirements: R10.13, R10.15_
- [x] 27.3 Derive and seed the Render_Ahead_Window
  - Implement `window = max(1, ceil(Playback_Speed_Ceiling / measuredRate) + 1)` reading the rate
    from the measurement records, seed the client configuration value the scheduler reads, and
    record the window in effect alongside the record it came from. This task is gated on tasks 27.1
    and 26.4: do not fix a window value before both the premium and the local neural rates are
    recorded.
  - _Requirements: R10.15, R11.21_
- [x] 27.4 Assert R9.7 and R10.5 remain achievable at the measured rate
  - Add a test that reads the recorded premium rate and fails with an explicit message when the
    30-second per-interaction bound or the 60-second provider-call bound cannot be met at that rate,
    so the requirement is revisited rather than the implementation stretched to fit (design
    tension 4).
  - _Requirements: R9.7, R10.5, R10.12_

## 28. The primary synthesis provider on SageMaker — NOT BEING BUILT

> **Closed 2026-08-29 at the product owner's direction.** Amazon Polly is the provider in effect
> and the only one being built. A SageMaker real-time endpoint bills for wall-clock time, so a
> GPU large enough for good voices costs on the order of $1,000 a month whether or not anyone is
> listening, against roughly $12 once per novel on Polly Generative — break-even near 85 books a
> month, far above beta volume. `SynthesisRouter` already treats an absent primary correctly, so
> adding SageMaker later is configuration rather than rework. Every task below is deliberately
> not being done; none is forgotten.

- [-] 28. Add the primary provider only after coverage is verified and the comparison is made
- [-] 28.1 Implement the billing-coverage check
  - Add a startup check and a `billing_coverage_verified` Narration_Measurement_Record that
    `SynthesisRouter` reads: while coverage of the chosen provider — including any AWS Marketplace
    model software charge levied separately by SageMaker JumpStart — is unrecorded, configure the
    Fallback_Synthesis_Provider as the provider in effect and deploy no Synthesis_Endpoint. Every
    task below is gated on this record existing.
  - _Requirements: R10.17, R10.18_
- [-] 28.2 Implement `SageMakerSynthesisProvider`
  - Add `backend/AudioChoice.Api/Processing/SageMakerSynthesisProvider.cs` invoking the
    Synthesis_Endpoint over SigV4 with the credential pair injected from Key Vault as a container app
    secret, registered as the primary behind `SynthesisRouter` and selectable only by configuration.
    Gated on task 28.1.
  - _Requirements: R10.1, R10.2_
- [-] 28.3 Write the provider comparison harness
  - Add a harness that renders one full Reference_Chapter with each provider using the same voice
    character and stores both renderings for side-by-side judgement of long-form pacing consistency
    and dialogue handling. Compare no sample shorter than a Reference_Chapter. The primary provider
    is not made the provider in effect until this comparison has been made.
  - _Requirements: R10.16_
- [-] 28.4 Write the Cold_Start_Delay benchmark harness
  - Add a harness that measures wall clock from a request reaching a scaled-to-zero
    Synthesis_Endpoint until its first audio sample, repeated enough times to record the spread, and
    writes a `cold_start_delay` Narration_Measurement_Record. Gated on tasks 28.1 and 28.2; the
    router's cold-start routing rule stays on the fallback until this record exists.
  - _Requirements: R10.6, R10.13_

## 29. Cross-cutting verification

- [x] 29. Prove the shipped paths still behave and the new invariants hold together
- [x] 29.1 Run the full Android and backend test suites and fix regressions
  - Run `./gradlew testExperimentalDebugUnitTest` (and the beta and release unit test variants) in
    `android-app/`, and `dotnet test` in `backend/`, and fix every failure introduced by this
    feature. Confirm the beta and release variants compile with no narration surface reachable.
  - _Requirements: R19.1, R19.2, R19.3_
- [x] 29.2 Write the end-to-end narration integration test
  - Add an integration test walking import, validation, text scan, plan construction, first-chapter
    render, playback, reader sync, a filter change with re-render, and book deletion against a fake
    backend and a fake `VoiceEngine`, asserting Book_Text offsets, Book_Time positions and filtered
    ranges agree at every step.
  - _Requirements: R4.3, R6.5, R12.12, R13.5, R15.8, R16.7_
- [x] 29.3 Write the guard-retention test suite
  - Group the filter-skip guard test, the completion guard test, the `DirectPlaybackTimeline`
    identity test and the narration-storage-outside-the-orphan-purge test into one suite with
    comments naming the failure each prevents, so removing a guard fails a named test rather than
    quietly changing behaviour for imported audiobooks.
  - _Requirements: R6.9, R12.16, R16.11, R19.3_
