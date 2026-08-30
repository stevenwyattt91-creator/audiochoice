# Requirements Document

## Introduction

AudioChoice today treats audio as the spine of a book. A listener imports an audio file, the file's
SHA-256 becomes the key for everything stored about that book, and an EPUB can be attached
afterwards as a reading companion: the server aligns the EPUB text to the audio timeline through
`POST /v1/reader/alignments`, and the app renders a read-along that follows the narration.

This feature inverts that relationship. When a listener owns an EPUB and no audiobook exists for
that title, AudioChoice synthesizes narration from the EPUB so the book can be listened to. The
EPUB becomes the spine and the audio is derived from the text.

Six consequences shape the requirements.

**Identity.** An EPUB-only book has no audio file to fingerprint. An EPUB has a SHA-256, a file
size and a file type, which is everything `BookFingerprint` requires, so the EPUB's own hash keys
the book. Duration is deliberately left absent, because a synthesized duration depends on which
voice rendered it and therefore describes the device rather than the edition.

**Filtering.** Content filtering is why AudioChoice exists, and it currently depends on a
server-side scan of transcribed audio. Here the source is text, so no transcription is needed, and
the character range of every flagged passage is known exactly rather than inferred. Filtered
passages are therefore excluded before synthesis: never spoken, never stored, never sent off the
device. The flagged spans use the same `ScanEvent` shape and the same category, group and event
identifiers as an audio scan, so `PlaybackFilterPredicate`, `PlaybackFilterTaxonomy`,
`BookFilterSettings` and the reader's `ReaderMasking` continue to work without modification, and a
listener's filter choices mean the same thing for a synthesized book as for an imported audiobook.

**Encryption.** Scope is EPUBs whose text can be read. An EPUB that encrypts only fonts, images or
media is perfectly narratable: those resources contribute nothing to Book_Text, so they are excluded
from extraction and the book proceeds. An EPUB that encrypts the package document, the navigation
document or a spine document is store DRM, the text cannot be read, and the file is declined with an
explanation of where DRM-free EPUBs come from. No decryption path is built in either case.

**Voice.** Narration climbs a three-rung ladder. The System_Voice is the Android text-to-speech
engine already on the phone: free, offline, always available, and the fallback whenever a better rung
cannot be used. The Local_Neural_Voice is an on-device neural model, still free and still offline,
with better quality at the cost of speed, and it is offered only where the device is measured to
synthesize faster than a listener can play it back. The Premium_Voice is synthesized on an
AudioChoice-owned Amazon SageMaker endpoint with Amazon Polly behind it, and requires a Premium_Tier
entitlement; the feature launches on Polly because Polly needs no infrastructure AudioChoice has to
operate. Neither the SageMaker endpoint nor Polly runs on the Transcription_GPU_Host, because
narration carries a playback deadline and transcription does not, and sharing one GPU would let a
scan queue delay a listener. Voice quality is the entire reason a product like Speechify has a
business, so the upgrade path matters; forcing every listener through it does not. Three numbers
this feature depends on are unverified today: what the premium endpoint synthesizes per second, how
long a scaled-to-zero endpoint takes to answer, and what a mid-range phone does with a neural model.
Requirement 10 requires each to be measured rather than assumed, and requires the choice between the
two premium providers to be made on a fully rendered chapter rather than on a short sample.

**Timing.** Narration renders one chapter at a time, and the first chapter becomes playable while
later chapters render behind it. Rendering a whole novel up front would mean an hours-long wait;
streaming sentence by sentence would mean no timeline ahead of the playhead, and `FilterSkipPlanner`,
scrubbing, chapter jumps, a real duration and the sleep timer all depend on one existing. Rendering
is bounded by a Render_Ahead_Window rather than run to the end of the book, for two reasons: it
bounds Premium_Voice server cost to the chapters actually listened to, and it bounds battery and CPU
time on device. A listener who wants the whole book on the plane can ask for it explicitly.

**Entitlement.** Premium_Tier is read from the existing `GET /v1/account/access` response, which is
backed by `IEntitlementStore`. Purchase verification is out of scope for this document: during the
experimental cycle Premium_Tier is granted through the existing administrative grant endpoint, and
Google Play Billing with server-side receipt verification is a prerequisite tracked separately before
Premium_Tier can be sold. Requirement 7 states that as a constraint rather than leaving it implied.

Two boundaries deserve explicit review.

The first is text processing. Producing filter spans requires content analysis that is not feasible
on device for a full-length novel, so Book_Text is sent to the AudioChoice backend, which sends
passages to a third-party model provider for classification exactly as `OpenAIContentAnalysisProvider`
already does for transcripts. Premium_Voice synthesis sends Spoken_Text to an AudioChoice-owned
Amazon SageMaker endpoint or, on fallback, to Amazon Polly. AudioChoice therefore does not promise
that text reaches no other party, because that would be false. It promises non-persistence and
purpose limitation, and Requirement 5 states those as testable constraints and requires the
first-use disclosure to name the categories of processor involved.

The second is release gating. Every requirement in this document applies to the experimental build
only. The `experimental` build type already exists in `android-app/app/build.gradle.kts` and is
created with `initWith(getByName("beta"))`, so no new variant is introduced; the `EXPERIMENTAL_BUILD`
flag is the gate, and Requirement 19 states that beta and release builds are unchanged.

Scope is the Android client and the AudioChoice backend.

## Glossary

- **Source_EPUB**: An EPUB file that a listener has selected for narration, addressed by a persisted
  Android content URI, and whose Text_Resources are unencrypted.
- **Book_Text**: The single flat string produced from a Source_EPUB by `EpubTextReader.read`. It is
  the coordinate space for every character offset in this document, and it is byte-for-byte stable
  for a given Source_EPUB and a given extraction version.
- **Book_Text_Language**: The language of Book_Text, being the language the Source_EPUB package
  metadata declares, or the device locale language when the Source_EPUB package metadata declares no
  language.
- **Narrated_Book**: A library book whose audio is synthesized from a Source_EPUB rather than
  imported as a file.
- **Imported_Audiobook**: A library book whose audio is a file the listener imported, which is the
  only kind of book AudioChoice supports before this feature.
- **EPUB_Validator**: The AudioChoice Android component that decides whether a selected file is a
  Source_EPUB, and that reports the reason when a selected file is not.
- **Text_Resource**: An EPUB archive entry that contributes to reading the book's text, being the
  package document, the EPUB 3 navigation document, the NCX document, or any spine document.
- **Non_Text_Resource**: Any EPUB archive entry that is not a Text_Resource, including a font, an
  image, an audio resource, a video resource and a style sheet.
- **Encrypted_Resource**: An EPUB archive entry named by a `CipherReference` element in the EPUB's
  `META-INF/encryption.xml` entry.
- **ADEPT_Encryption**: Adobe Content Server encryption, declared by an `EncryptedData` element in
  the EPUB's `META-INF/encryption.xml` entry.
- **Store_DRM**: Encryption declared over one or more Text_Resources, including ADEPT_Encryption over
  a Text_Resource.
- **Font_Obfuscation**: An EPUB font obfuscation scheme declared over a Non_Text_Resource.
- **Structure_Parser**: The AudioChoice Android component that divides Book_Text into
  Narration_Chapters and classifies each region of Book_Text as prose or as a Non_Prose_Block.
- **Non_Prose_Block**: A region of Book_Text that the Structure_Parser classifies as unsuited to
  narration: a table, a code block, a footnote body, an image description, a page number, or a
  running header or footer.
- **Narration_Chapter**: An ordered division of Book_Text carrying a title, a first character offset
  and a last character offset, derived from the Source_EPUB navigation document or from the
  Source_EPUB spine.
- **Narration_Unit**: One sentence-scale span of Book_Text queued for synthesis, carrying its
  Source_Range and the Spoken_Text derived from that range.
- **Source_Range**: A half-open pair of character offsets into Book_Text, written
  `[startCharacter, endCharacter)`, matching the coordinate space of `ReaderParagraph` and
  `ReaderTimingRange`.
- **Spoken_Text**: The exact character sequence handed to a Voice_Engine for one Narration_Unit,
  after Pronunciation_Rules have been applied and after Filtered_Ranges have been excluded.
- **Synthesis_Input_Limit**: The greatest character count of one Spoken_Text, being the lesser of
  1,000 characters and the value `TextToSpeech.getMaxSpeechInputLength` reports, so that a
  Narration_Plan is independent of the Selected_Voice.
- **Narration_Plan**: The ordered, persisted list of Narration_Chapters and their Narration_Units
  for one Narrated_Book, together with the Plan_Inputs that produced the list and a record of
  whether chapter derivation fell back to the spine.
- **Plan_Inputs**: The values a Narration_Plan depends on: the Source_EPUB SHA-256, the Book_Text
  hash, the Plan_Version, the Synthesis_Input_Limit, the set of enabled Text_Scan_Events, and the
  Pronunciation_Rules in effect.
- **Plan_Version**: An integer that AudioChoice increments whenever Structure_Parser or
  Narration_Plan construction behaviour changes, serving the same purpose for narration that
  `READER_ALIGNMENT_VERSION` serves for reader alignment.
- **Voice_Engine**: A component that converts Spoken_Text into audio samples. Exactly three
  implementations exist: the System_Voice, the Local_Neural_Voice and the Premium_Voice.
- **System_Voice**: A Voice_Engine backed by an Android `TextToSpeech` engine installed on the
  listener's device, permitted in the Free_Tier, performing synthesis without network access, and
  serving as the fallback Voice_Engine whenever another Voice_Engine is unavailable.
- **Local_Neural_Voice**: A Voice_Engine backed by a neural synthesis model held on the listener's
  device as an application asset of 100 MB or less, permitted in the Free_Tier, performing synthesis
  without network access, and offered only where its measured Synthesis_Rate satisfies Requirement 8.
- **Premium_Voice**: A Voice_Engine backed by synthesis performed on AudioChoice-owned Amazon Web
  Services infrastructure through a Synthesis_Provider, permitted in the Premium_Tier only.
- **Selected_Voice**: The Voice_Engine and voice identifier recorded for one Narrated_Book.
- **Synthesis_Rate**: The ratio of the duration of audio a Voice_Engine produces to the wall-clock
  time that Voice_Engine takes to produce it, expressed as a dimensionless multiple of real time.
- **Playback_Speed_Ceiling**: 2.0, the greatest playback speed the Player's speed control offers,
  matching `MAXIMUM_SPEED` in `LocalAudioStore`.
- **Synthesis_Rate_Margin**: 1.5, the factor by which a Voice_Engine's measured Synthesis_Rate must
  exceed the Playback_Speed_Ceiling before AudioChoice offers that Voice_Engine on a device.
- **Account_Access**: The `AccountAccessResponse` the AudioChoice backend returns from
  `GET /v1/account/access`, carrying `isActive`, `plan`, `source` and `expiresAt` for the signed-in
  account, and produced by the existing `IEntitlementStore`.
- **Narration_Tier**: Either the Free_Tier or the Premium_Tier, being the tier AudioChoice derives
  from Account_Access for the signed-in account.
- **Free_Tier**: The Narration_Tier in effect when Account_Access reports no active entitlement,
  permitting the System_Voice and the Local_Neural_Voice.
- **Premium_Tier**: The Narration_Tier in effect when Account_Access reports an active entitlement,
  permitting the System_Voice, the Local_Neural_Voice and the Premium_Voice.
- **Tier_Grace_Period**: 7 days, measured from the most recent successful read of Account_Access, for
  which the last recorded Narration_Tier remains in effect while Account_Access cannot be read.
- **Premium_Voice_Acknowledgement**: A listener's recorded, account-scoped acknowledgement that
  selecting the Premium_Voice sends a book's Spoken_Text off the device for synthesis, carrying an
  agreement version, the agreement text and an acceptance timestamp.
- **Narration_Renderer**: The AudioChoice Android component that drives a Voice_Engine over a
  Narration_Plan and produces Chapter_Audio and Chapter_Timelines.
- **Render_Queue**: The ordered list of Narration_Chapters awaiting rendering for one
  Narrated_Book, together with each chapter's Render_State.
- **Render_State**: One of the mutually exclusive states of a Narration_Chapter: not rendered,
  rendering, rendered, or render failed.
- **Render_Ahead_Window**: The count of Narration_Chapters that AudioChoice keeps in the rendered
  Render_State after the Narration_Chapter containing the current playback position, being at least 1
  and having a value derived by the measurement Requirement 10 requires.
- **Full_Book_Render_Request**: A listener's recorded request that every Narration_Chapter of one
  Narrated_Book be rendered regardless of the Render_Ahead_Window.
- **Chapter_Audio**: One app-private audio file holding the rendered narration of exactly one
  Narration_Chapter.
- **Chapter_Timeline**: The ordered list of `ReaderTimingRange` values mapping each rendered
  Narration_Unit's Source_Range to its start and end time within one Chapter_Audio.
- **Narration_Timeline**: The Chapter_Timelines of a Narrated_Book, offset into Book_Time and
  concatenated, giving one ordered list of `ReaderTimingRange` values for the whole book.
- **Book_Time**: Elapsed time from the first audio sample of the first rendered Narration_Chapter,
  measured in seconds, independent of playback speed.
- **Narration_Duration**: The sum of the durations of every rendered Chapter_Audio for one
  Narrated_Book, expressed in seconds, covering the Narration_Chapters currently in the rendered
  Render_State and therefore growing as rendering progresses.
- **Synthesis_Provider**: The AudioChoice backend seam that converts Spoken_Text into Chapter_Audio,
  mirroring the `ITranscriptionProvider` seam and selected by configuration in the manner
  `FasterWhisperTranscriptionProvider` and `OpenAITranscriptionProvider` are selected. Exactly two
  implementations exist: the Primary_Synthesis_Provider and the Fallback_Synthesis_Provider.
- **Primary_Synthesis_Provider**: A Synthesis_Provider backed by a frontier text-to-speech model
  deployed to the Synthesis_Endpoint through Amazon SageMaker JumpStart, whose leading candidate
  model is Cartesia Sonic 3, and whose cost is billed as Amazon SageMaker instance-hours rather than
  per character of Spoken_Text.
- **Fallback_Synthesis_Provider**: A Synthesis_Provider backed by Amazon Polly, whose candidate
  engine for narration quality is Polly Long-form and whose lower-cost engine is Polly Generative,
  Polly Generative being the implementation this feature launches with because it requires no
  AudioChoice-operated infrastructure.
- **Synthesis_Endpoint**: The AudioChoice-owned Amazon SageMaker inference endpoint that hosts the
  Primary_Synthesis_Provider model.
- **Cold_Start_Delay**: The wall-clock time from a synthesis request reaching a Synthesis_Endpoint
  that is scaled to zero instances until that Synthesis_Endpoint returns the first audio sample of
  that request.
- **Transcription_GPU_Host**: The Lambda-hosted GPU host that runs the
  `faster-whisper-large-v3-turbo` transcription service for the existing scan pipeline.
- **Reference_Chapter**: A Narration_Chapter of continuous prose holding between 15,000 and 25,000
  characters of Spoken_Text, rendered in full by a Synthesis_Provider for the provider comparison
  Requirement 10 requires.
- **Mid_Range_Device**: An Android device carrying 6 GB of RAM and 8 CPU cores whose
  system-on-chip was released 3 to 5 years before the measurement, being the device class on which
  the Local_Neural_Voice Synthesis_Rate is measured.
- **AWS_Billing_Arrangement**: The Amazon Web Services billing arrangement available to AudioChoice,
  covering Amazon Web Services infrastructure charges together with any third-party model software
  charge levied through AWS Marketplace for a model deployed by Amazon SageMaker JumpStart.
- **Narration_Measurement_Record**: A persisted record of a value this document requires to be
  measured, carrying the measured value, the measurement date, the instance type or device measured,
  and the version of the software measured.
- **Narration_Object_Store**: The object storage service to which the AudioChoice backend writes
  Premium_Voice Chapter_Audio and from which the AudioChoice Android application downloads it.
- **Text_Scan**: The AudioChoice backend operation that reads Book_Text and returns
  Text_Scan_Events.
- **Analysis_Processor**: The third-party model provider to which the AudioChoice backend sends
  Book_Text passages for classification during a Text_Scan, being the processor
  `OpenAIContentAnalysisProvider` already uses for transcript classification.
- **Text_Scan_Event**: A flagged span of Book_Text, expressed as a `ScanEvent` whose `startTime` and
  `endTime` carry the flagged Source_Range's start and end character offsets, and whose
  `categoryID`, `groupID`, `eventID`, `stableKey` and `aggregateKey` are drawn from the same content
  taxonomy an audio scan uses.
- **Text_Scan_Acknowledgement**: A listener's recorded acknowledgement of the statement that
  Book_Text is sent to the AudioChoice backend for scanning, that the backend sends passages to the
  Analysis_Processor, and that no part of Book_Text is stored, carrying a statement version, the
  statement text and an acceptance timestamp.
- **Enabled_Text_Scan_Event**: A Text_Scan_Event that `PlaybackFilterPredicate.isEnabled` reports as
  enabled for the listener's current filter choices.
- **Filtered_Range**: A Source_Range covered by an Enabled_Text_Scan_Event, after overlapping ranges
  have been merged.
- **Pronunciation_Rule**: A listener-supplied pair of a written form and a replacement form, applied
  to Spoken_Text before synthesis and excluded from Book_Text, carrying a scope of either one
  Narrated_Book or the account.
- **Narration_Store**: The AudioChoice Android component that persists the Narration_Plan,
  Chapter_Audio, Chapter_Timelines, Render_Queue, Selected_Voice, Pronunciation_Rules and
  Text_Scan_Events for a Narrated_Book.
- **Player**: The existing AudioChoice playback subsystem, comprising `AudioChoicePlaybackService`
  and `PlayerViewModel`.
- **Reader**: The existing AudioChoice read-along subsystem, comprising `ReaderParagraphParser`,
  `ReaderSync` and `ReaderMasking`.
- **Storage_Reserve**: The quantity of free device storage AudioChoice leaves unused, fixed at
  1.0 GB.
- **Experimental_Build**: A build of the AudioChoice Android application produced from the
  `experimental` build type declared in `android-app/app/build.gradle.kts`, in which
  `BuildConfig.EXPERIMENTAL_BUILD` is true.

## Requirements

### Requirement 1: Import an EPUB that has no audiobook

**User Story:** As a listener who owns an ebook but no audiobook of a title, I want to add the EPUB
to AudioChoice, so that I can listen to that book instead of only reading it.

#### Acceptance Criteria

1. THE AudioChoice Android application SHALL present a library import action that offers files of
   media type `application/epub+zip` and files whose name ends in `.epub`, and SHALL create a
   Narrated_Book from a selected file only after the EPUB_Validator accepts that file.
2. WHEN a listener selects a Source_EPUB through the library import action, THE
   AudioChoice Android application SHALL take a persistable read permission on that file's content
   URI before reading Book_Text, and SHALL persist that content URI against the Narrated_Book.
3. WHEN a listener selects a Source_EPUB through the library import action, THE
   AudioChoice Android application SHALL compute, off the Android main thread and within
   30.0 seconds for a Source_EPUB of 50 MB or smaller, a `BookFingerprint` whose `sha256` is the
   SHA-256 of every byte of the Source_EPUB, whose `fileSize` is the Source_EPUB byte count, whose
   `fileType` is `epub`, and whose `duration` is absent.
4. WHEN a listener selects a Source_EPUB through the library import action, THE
   AudioChoice Android application SHALL read the title and the author from the Source_EPUB package
   metadata, SHALL record the first title and the first author in document order against the
   Narrated_Book, and SHALL truncate each recorded value to its first 500 characters.
5. IF the Source_EPUB package metadata carries no title, THEN THE AudioChoice Android application
   SHALL derive the Narrated_Book title from the Source_EPUB filename with its final `.epub`
   extension removed, or from the first 8 characters of the Source_EPUB SHA-256 when the content URI
   supplies no filename, SHALL truncate that title to its first 500 characters, and SHALL record
   that the title was derived rather than read from package metadata.
6. WHERE the Source_EPUB carries a cover image in its package manifest, THE
   AudioChoice Android application SHALL store that image as the Narrated_Book cover through the
   existing book cover storage path.
7. WHEN a listener attaches an EPUB to an open Imported_Audiobook, THE AudioChoice Android
   application SHALL keep the existing reader-attachment behaviour and SHALL create no
   Narrated_Book.
8. IF a listener selects a Source_EPUB whose SHA-256 matches the `sha256` of an existing
   Narrated_Book, THEN THE AudioChoice Android application SHALL open that existing Narrated_Book,
   SHALL create no second library entry, SHALL replace the persisted content URI of that
   Narrated_Book with the newly selected content URI, SHALL keep that Narrated_Book's
   Narration_Plan, Chapter_Audio and playback position unchanged, and SHALL report that the book is
   already in the library.
9. THE Narration_Store SHALL key every value it persists for a Narrated_Book by the Source_EPUB
   SHA-256, so that the Narrated_Book uses the same per-book key space as an Imported_Audiobook.
10. IF the AudioChoice Android application cannot take a persistable read permission on a selected
    file's content URI, THEN THE AudioChoice Android application SHALL create no Narrated_Book,
    SHALL persist no Narration_Plan, and SHALL report that the file could not be opened for reading.
11. IF the Source_EPUB package metadata carries no author, THEN THE AudioChoice Android application
    SHALL record the Narrated_Book author as absent, SHALL present the Narrated_Book with no author,
    and SHALL complete the import.
12. IF the Source_EPUB package manifest declares no cover image, or the declared cover image cannot
    be decoded as an image, THEN THE AudioChoice Android application SHALL store no cover for the
    Narrated_Book, SHALL present the existing default library cover for that Narrated_Book, and
    SHALL complete the import.

### Requirement 2: Accept EPUBs whose text can be read and decline the rest

**User Story:** As a listener whose ebook came from a store that encrypts its files, I want to be
told plainly whether AudioChoice can narrate that file, why not when it cannot, and where a file
that does work comes from, so that I am not left guessing why nothing happened.

#### Acceptance Criteria

1. WHEN a listener selects a file for narration, THE EPUB_Validator SHALL read the file's
   `META-INF/container.xml` entry, SHALL confirm that the entry names a package document, SHALL
   confirm that the named package document is present in the archive, and SHALL complete every
   check of acceptance criteria 2 through 8 before the AudioChoice Android application creates a
   library entry for that file.
2. WHERE a selected file's `META-INF/encryption.xml` entry declares encryption over
   Non_Text_Resources only, THE EPUB_Validator SHALL accept the file, and THE AudioChoice Android
   application SHALL exclude every Encrypted_Resource of that file from Book_Text extraction.
3. WHERE a selected file's `META-INF/encryption.xml` entry declares Font_Obfuscation only, THE
   EPUB_Validator SHALL treat the file as carrying no encryption, that case being one instance of
   acceptance criterion 2.
4. WHERE a selected file carries no `META-INF/encryption.xml` entry, THE EPUB_Validator SHALL treat
   the file as carrying no encryption.
5. IF a selected file carries Store_DRM, THEN THE EPUB_Validator SHALL decline the file, SHALL
   report that the store that sold the file protected its text and that the text therefore cannot be
   read, SHALL name which of the package document, the navigation document and the spine documents
   are encrypted, SHALL name at least three sources of DRM-free EPUBs, and SHALL leave the selected
   file's bytes unmodified.
6. IF a selected file's content URI cannot be opened for reading, or the file's ZIP central
   directory cannot be read, THEN THE EPUB_Validator SHALL decline the file and SHALL report that
   the file could not be opened and that narration requires an EPUB whose text is unencrypted.
7. IF Book_Text extracted from a selected file contains fewer than 500 characters that are letters
   or digits, THEN THE EPUB_Validator SHALL decline the file, SHALL report that the file contains
   too little text to narrate, and SHALL state the 500-character minimum.
8. IF every spine document listed in a selected file's package document is absent from the archive,
   is an Encrypted_Resource, or cannot be parsed as XML or HTML, THEN THE EPUB_Validator SHALL
   decline the file and SHALL report that the file's text could not be read.
9. IF a selected file carries no `META-INF/container.xml` entry, or that entry names no package
   document, or the named package document is absent from the archive, THEN THE EPUB_Validator
   SHALL decline the file and SHALL report that the file is not an EPUB that AudioChoice can read.
10. WHEN the EPUB_Validator declines a file, THE EPUB_Validator SHALL report exactly one reason to
    the listener, selected in the order stated by acceptance criteria 6, 9, 5, 8 and 7.
11. WHEN the EPUB_Validator declines a file, THE AudioChoice Android application SHALL create no
    library entry, SHALL persist no Narration_Plan, SHALL retain no Book_Text, extracted spine
    content or cover image from that file, and SHALL release any read permission it took on that
    file.
12. WHEN the EPUB_Validator declines a file for Store_DRM under acceptance criterion 5, THE
    AudioChoice Android application SHALL delete every character of Book_Text and every archive
    resource it extracted from that file before the Store_DRM check completed, and SHALL retain no
    such character or resource after reporting the decline.
13. THE AudioChoice Android application SHALL perform no operation that decrypts, circumvents or
    removes ADEPT_Encryption.
14. WHEN the EPUB_Validator declines a file for Store_DRM under acceptance criterion 5, THE
    AudioChoice Android application SHALL state that a Kindle title purchased from Amazon and
    published without DRM can be downloaded as an EPUB by the verified purchaser from Amazon's
    Manage Your Content and Devices page, and SHALL present a control that opens that page.
15. WHEN the EPUB_Validator declines a file for Store_DRM under acceptance criterion 5, THE
    AudioChoice Android application SHALL state that a Kindle Unlimited borrow is a loan rather than
    a purchase and therefore offers no EPUB download.
16. WHEN the EPUB_Validator declines a file for Store_DRM under acceptance criterion 5, THE
    AudioChoice Android application SHALL state that whether a title is available without DRM is the
    publisher's or the author's choice rather than an AudioChoice restriction.
17. WHEN a listener selects a file for narration, THE EPUB_Validator SHALL complete every check of
    acceptance criteria 1 through 9 within 5.0 seconds for a file of 100 MB or fewer, measured on a
    device with four or more CPU cores, and SHALL run off the Android main thread.

### Requirement 3: Divide the book into chapters and set aside content that should not be spoken

**User Story:** As a listener, I want synthesized narration to begin at the story and to read
chapters in the order the author intended, so that listening to a synthesized book feels like
listening to an audiobook rather than like hearing a file read aloud.

#### Acceptance Criteria

1. WHERE a Source_EPUB carries an EPUB 3 navigation document, WHEN the Structure_Parser processes
   that Source_EPUB, THE Structure_Parser SHALL derive one Narration_Chapter per top-level entry of
   that navigation document's `toc` nav element, ignoring entries nested below the top level and
   ignoring entries whose target resolves to no spine document.
2. WHERE a Source_EPUB carries no EPUB 3 navigation document and carries an NCX document, WHEN the
   Structure_Parser processes that Source_EPUB, THE Structure_Parser SHALL derive one
   Narration_Chapter per top-level `navPoint` of that NCX document's `navMap`, ignoring `navPoint`
   elements nested below the top level and ignoring `navPoint` elements whose target resolves to no
   spine document.
3. WHERE a Source_EPUB carries neither an EPUB 3 navigation document nor an NCX document, WHEN the
   Structure_Parser processes that Source_EPUB, THE Structure_Parser SHALL derive one
   Narration_Chapter per spine document that contributes at least one character to Book_Text.
4. THE Structure_Parser SHALL order Narration_Chapters by the spine order of the Source_EPUB
   package document, and SHALL order Narration_Chapters derived from the same spine document by
   ascending start character offset.
5. THE Structure_Parser SHALL produce Narration_Chapters whose Source_Ranges are ordered,
   non-overlapping, each at least one character long, and together cover every character offset of
   Book_Text, assigning to the first Narration_Chapter every character that precedes the first
   derived chapter boundary.
6. THE Structure_Parser SHALL classify as Non_Prose_Blocks the regions of Book_Text derived from
   `table`, `pre`, `code`, `figcaption` and `img` elements, from elements carrying the EPUB
   structural semantics `footnote`, `endnote`, `pagebreak`, `noteref` or `toc`, and from elements
   carrying the ARIA roles `doc-footnote`, `doc-endnote` or `doc-pagebreak`, and SHALL extend each
   such region over the text contributed by that element's descendants.
7. THE Structure_Parser SHALL record for every Narration_Chapter a title of 1 to 200 characters,
   with leading and trailing whitespace removed, with each internal run of whitespace collapsed to
   one space, and with a source title longer than 200 characters truncated to 200 characters.
8. WHEN the Structure_Parser records a Narration_Chapter, THE Narration_Store SHALL persist that
   chapter as an `AudioChapter` whose `title` is the Narration_Chapter title and whose position in
   the persisted chapter list matches the Narration_Chapter's position in the ordered
   Narration_Chapters, so that the Player presents narration chapters through the existing chapter
   control in the order the Narration_Plan records.
9. THE Structure_Parser SHALL complete within 5.0 seconds for a Source_EPUB whose Book_Text is
   1,000,000 characters or fewer, measured on a device with four or more CPU cores.
10. THE Structure_Parser SHALL run off the Android main thread.
11. IF the source document supplies no title for a Narration_Chapter, or supplies a title that is
    empty after the whitespace handling stated in acceptance criterion 7, THEN THE Structure_Parser
    SHALL record a title derived from that Narration_Chapter's 1-based ordinal position among the
    ordered Narration_Chapters.
12. IF a Source_EPUB's EPUB 3 navigation document or NCX document cannot be parsed, yields no entry
    that resolves to a spine document, or yields more than 2,000 Narration_Chapters, THEN THE
    Structure_Parser SHALL derive one Narration_Chapter per spine document that contributes at least
    one character to Book_Text and SHALL record against the Narration_Plan that chapter derivation
    fell back to the spine.
13. THE Structure_Parser SHALL classify as Non_Prose_Blocks the regions of Book_Text derived from
    spine documents or elements carrying the EPUB structural semantics `cover`, `titlepage`,
    `copyright-page`, `colophon`, `landmarks`, `loi` or `lot`, so that narration begins at the
    book's prose rather than at its front matter.

### Requirement 4: Build a narration plan whose spans map back to the book text exactly

**User Story:** As a listener, I want the highlighted text, the audio position and the filtered
passages to agree with each other at every moment, so that read-along and filtering are trustworthy
rather than approximate.

#### Acceptance Criteria

1. THE Structure_Parser SHALL divide each Narration_Chapter's prose into Narration_Units at sentence
   boundaries, SHALL divide a sentence whose character count exceeds the Synthesis_Input_Limit into
   Narration_Units at clause boundaries, and SHALL divide at the last word boundary at or before the
   Synthesis_Input_Limit any clause whose character count still exceeds it, so that every
   Narration_Unit's Spoken_Text holds between 1 character and the Synthesis_Input_Limit inclusive.
2. THE Structure_Parser SHALL produce Narration_Units whose Source_Ranges, within one
   Narration_Chapter, each satisfy `startCharacter` < `endCharacter`, fall entirely within that
   Narration_Chapter's Source_Range, are ordered by strictly increasing `startCharacter`, and
   satisfy `endCharacter` no greater than the next Narration_Unit's `startCharacter`.
3. FOR ALL Narration_Units of a Narration_Plan, `startCharacter` SHALL be at least 0,
   `endCharacter` SHALL be no greater than the Book_Text character count, and
   `Book_Text.substring(startCharacter, endCharacter)` SHALL equal the Narration_Unit's recorded
   source characters character for character, so that a Narration_Unit indexes Book_Text without
   rewriting Book_Text.
4. THE Structure_Parser SHALL produce no Narration_Unit whose Source_Range overlaps any
   Non_Prose_Block by one or more characters.
5. THE Structure_Parser SHALL produce Narration_Units whose Spoken_Text contains at least one letter
   or digit, and SHALL record no Narration_Unit for a prose span whose characters are all whitespace
   or punctuation.
6. THE Narration_Store SHALL serialize a Narration_Plan to JSON and SHALL deserialize a
   Narration_Plan from JSON, completing each of those two operations within 2.0 seconds for a
   Narration_Plan holding 20,000 Narration_Units.
7. FOR ALL Narration_Plans, deserializing the serialization of a Narration_Plan SHALL produce a
   Narration_Plan equal to the original (round-trip property), where equal means the same
   Narration_Chapter count and order, the same Narration_Chapter titles and Source_Ranges, the same
   Narration_Unit count and order within each Narration_Chapter, the same Narration_Unit
   Source_Ranges and Spoken_Text, and the same value for every member of Plan_Inputs.
8. FOR ALL Narration_Timelines, deserializing the serialization of a Narration_Timeline SHALL
   produce a Narration_Timeline equal to the original (round-trip property), where equal means the
   same `ReaderTimingRange` count and order, the same start and end character offsets for every
   value, and start and end times that differ from the original by no more than 1 millisecond.
9. WHEN the Narration_Store loads a Narration_Plan whose recorded Plan_Version differs from the
   current Plan_Version, THE Narration_Store SHALL discard that Narration_Plan, SHALL report the
   Narrated_Book as requiring a new Narration_Plan, and SHALL keep that Narrated_Book's
   Text_Scan_Events, because Book_Text is unchanged.
10. WHEN the Narration_Store loads a Narration_Plan whose recorded Book_Text hash differs from the
    hash of the current Book_Text, THE Narration_Store SHALL discard that Narration_Plan and SHALL
    report the Narrated_Book as requiring both a new Narration_Plan and a new Text_Scan, because
    every recorded character offset is expressed in the coordinate space of the previous Book_Text.
11. THE Structure_Parser SHALL produce, for the same Book_Text and the same Plan_Inputs,
    Narration_Plans that are equal on every run by the equality stated in acceptance criterion 7
    (idempotence property).
12. IF the Narration_Store cannot deserialize a persisted Narration_Plan, THEN THE Narration_Store
    SHALL discard that Narration_Plan, SHALL report the Narrated_Book as requiring a new
    Narration_Plan, and SHALL keep the Narrated_Book library entry and its Source_EPUB content URI.
13. WHERE a Narration_Chapter's prose yields no Narration_Unit, THE Structure_Parser SHALL record
    that Narration_Chapter in the Narration_Plan with zero Narration_Units and SHALL record that the
    Narration_Chapter requires no rendering.
14. IF a Narration_Plan would hold zero Narration_Units across every Narration_Chapter, THEN THE
    AudioChoice Android application SHALL persist no Narration_Plan, SHALL report that the
    Source_EPUB contains no prose that can be narrated, and SHALL keep the Narrated_Book unrendered.

### Requirement 5: Scan the book text for filtered content and state honestly who processes it

**User Story:** As a listener who uses AudioChoice because it filters content, I want a synthesized
book to be filtered by the same controls as my imported audiobooks and to be told accurately where
my book's text goes, so that turning off a category means the same thing whichever kind of book I am
listening to and so that the privacy claim I am given is true.

#### Acceptance Criteria

1. WHEN a listener creates a Narrated_Book, THE AudioChoice Android application SHALL request
   exactly one Text_Scan of that Narrated_Book's complete Book_Text from the AudioChoice backend,
   before submitting any Narration_Unit to a Voice_Engine.
2. THE AudioChoice backend SHALL return Text_Scan_Events whose `categoryID`, `groupID`, `eventID`,
   `stableKey`, `aggregateKey` and `aggregateDisplay` are drawn from the same content taxonomy
   version that an audio scan uses.
3. THE AudioChoice backend SHALL return Text_Scan_Events whose `startTime` is the flagged
   Source_Range's start character offset and whose `endTime` is the flagged Source_Range's end
   character offset, each satisfying `0 <= startTime < endTime <= Book_Text character count`.
4. THE AudioChoice backend SHALL hold Book_Text in memory only for the duration of one Text_Scan
   request, SHALL complete or fail that request within 120 seconds of receiving Book_Text, and SHALL
   retain no character of Book_Text after that request returns, whether the request succeeded or
   failed.
5. THE AudioChoice backend SHALL write no character of Book_Text to any persistent store, log file,
   cache, checkpoint or telemetry record.
6. THE AudioChoice backend SHALL persist, from a Text_Scan, only the Text_Scan_Events, the scan
   date, the scanner version and the Source_EPUB fingerprint, and SHALL include no character of
   Book_Text in the Text_Scan response or in any record it persists.
7. THE AudioChoice backend SHALL send Book_Text passages to the Analysis_Processor for
   classification, SHALL send Book_Text to no processor other than the Analysis_Processor, and SHALL
   send Spoken_Text to no processor other than a Synthesis_Provider (purpose limitation).
8. WHEN a listener creates their first Narrated_Book, THE AudioChoice Android application SHALL
   present a statement that the book's text is sent to AudioChoice to produce filter results, that
   AudioChoice sends passages of that text to a third-party model provider that performs the
   classification, that AudioChoice holds that text only while the scan runs, that AudioChoice
   stores no part of that text, and that AudioChoice uses that text for no purpose other than
   producing filter results.
9. WHEN the AudioChoice Android application presents the statement of acceptance criterion 8, THE
   AudioChoice Android application SHALL name each category of processor involved, being the
   AudioChoice backend, the third-party model provider that classifies content, and, WHERE the
   Premium_Voice is selected, the AudioChoice-owned Synthesis_Endpoint hosted by Amazon SageMaker
   and Amazon Polly as the Fallback_Synthesis_Provider.
10. THE AudioChoice Android application SHALL determine which Text_Scan_Events are
    Enabled_Text_Scan_Events by calling `PlaybackFilterPredicate.isEnabled`.
11. THE AudioChoice Android application SHALL present the filter controls for a Narrated_Book
    through `PlaybackFilterTaxonomy`, so that a Narrated_Book and an Imported_Audiobook present the
    same control tree.
12. THE AudioChoice Android application SHALL read and write a Narrated_Book's filter choices
    through `BookFilterSettings`, so that a listener's choices synchronise across devices by the
    same path as an Imported_Audiobook's choices.
13. IF a Text_Scan request returns an error, loses connectivity, or returns no response within
    120 seconds, THEN THE AudioChoice Android application SHALL retry that request up to three
    times with an increasing delay, and after the third failed attempt SHALL report that filter
    results are unavailable for the Narrated_Book, SHALL offer to request the Text_Scan again, and
    SHALL keep the Narrated_Book unrendered until a Text_Scan completes or the listener chooses to
    continue without filter results.
14. WHERE a listener chooses to continue without filter results, THE AudioChoice Android application
    SHALL record that the Narrated_Book has no filter results, SHALL present that state wherever it
    presents the Narrated_Book in the library list and on the Narrated_Book detail surface, and
    SHALL keep that state until a Text_Scan completes for that Narrated_Book.
15. WHEN the AudioChoice Android application applies filter results for a Narrated_Book, THE
    AudioChoice Android application SHALL read the Text_Scan_Events stored locally against that
    Narrated_Book's Source_EPUB SHA-256 together with their recorded scanner version, and SHALL
    request no further Text_Scan while that recorded scanner version equals the current scanner
    version, so that filtering continues to work without network access.
16. WHILE no Text_Scan_Acknowledgement is recorded, THE AudioChoice Android application SHALL
    request no Text_Scan, SHALL send no Book_Text to the AudioChoice backend, and SHALL keep the
    Narrated_Book unrendered.
17. WHEN a listener acknowledges the statement of acceptance criterion 8, THE AudioChoice Android
    application SHALL record a Text_Scan_Acknowledgement carrying the statement version, the
    statement text and the acceptance timestamp.
18. IF the AudioChoice backend returns a Text_Scan_Event whose start character offset is not less
    than its end character offset, or whose end character offset exceeds the Book_Text character
    count, THEN THE AudioChoice Android application SHALL discard the returned Text_Scan_Events,
    SHALL treat that Text_Scan as not completed, and SHALL apply acceptance criterion 13.

### Requirement 6: Leave filtered passages out of the narration

**User Story:** As a listener who has turned a category off, I want the excluded passages never to
be spoken at all, so that no filtered content is synthesized, stored on my device, or sent off my
device.

#### Acceptance Criteria

1. THE Narration_Renderer SHALL derive Filtered_Ranges by merging the Source_Ranges of every
   Enabled_Text_Scan_Event into an ordered list of non-overlapping Source_Ranges, merging two
   Source_Ranges whenever they overlap and whenever one Source_Range's start offset equals the
   other's end offset, and SHALL complete that derivation within 1.0 second for a Book_Text of
   1,000,000 characters carrying up to 10,000 Enabled_Text_Scan_Events.
2. THE Narration_Renderer SHALL submit to a Voice_Engine no character of Book_Text whose offset
   falls within a Filtered_Range.
3. THE Narration_Renderer SHALL include no character of a Filtered_Range in any request it sends to
   the AudioChoice backend for Premium_Voice synthesis.
4. IF a Filtered_Range covers a Narration_Unit's Source_Range in full, THEN THE Narration_Renderer
   SHALL omit that Narration_Unit from the Render_Queue, SHALL submit no Spoken_Text for that
   Narration_Unit to any Voice_Engine, and SHALL record no Chapter_Timeline entry for that
   Narration_Unit.
5. WHERE a Filtered_Range covers part of a Narration_Unit's Source_Range, THE Narration_Renderer
   SHALL submit as one Spoken_Text the uncovered characters of that Narration_Unit, concatenated in
   ascending offset order with one space at each boundary where characters were removed, and SHALL
   record one Chapter_Timeline entry whose Source_Range is that Narration_Unit's whole Source_Range.
6. FOR ALL Narration_Plans and all pairs of filter choices, a set of Enabled_Text_Scan_Events that
   is a superset of another set SHALL produce a Narration_Duration no greater than the smaller set
   produces (metamorphic property).
7. WHEN the Narration_Renderer begins rendering a Narration_Chapter of a Narrated_Book for which no
   Enabled_Text_Scan_Event exists, THE Narration_Renderer SHALL submit every Narration_Unit that
   Narration_Chapter records, with no character removed.
8. WHEN a Narration_Chapter reaches the rendered Render_State, THE Narration_Renderer SHALL record
   against that Narration_Chapter, through the Narration_Store, the count of Narration_Units it
   omitted in full and the count of Narration_Units from which it removed part of the text.
9. WHILE a Narrated_Book is playing, THE Player SHALL plan no filter skip from a Text_Scan_Event,
   because the narration of a Narrated_Book contains no Filtered_Range.
10. IF an Enabled_Text_Scan_Event carries a start offset below 0, an end offset above the Book_Text
    character count, or an end offset no greater than its start offset, THEN THE Narration_Renderer
    SHALL submit no Narration_Unit of that Narrated_Book to a Voice_Engine, SHALL report that the
    Narrated_Book's filter results cannot be applied, and SHALL offer to request the Text_Scan
    again.
11. IF the characters of a Narration_Unit left uncovered by Filtered_Ranges contain no letter and no
    digit, THEN THE Narration_Renderer SHALL treat that Narration_Unit as covered in full and SHALL
    apply acceptance criterion 4 to that Narration_Unit.
12. IF every Narration_Unit of a Narration_Chapter is omitted in full, THEN THE Narration_Renderer
    SHALL write no Chapter_Audio for that Narration_Chapter, SHALL record an empty Chapter_Timeline
    for that Narration_Chapter, SHALL set that Narration_Chapter's Render_State to rendered, and
    SHALL add 0.0 seconds to the Narration_Duration.

### Requirement 7: Gate narration voices on the account's entitlement

**User Story:** As a listener, I want the free voices to work without an account decision and the
premium voice to follow the entitlement my account actually holds, so that what I can use is clear
and does not depend on my device deciding for itself.

#### Acceptance Criteria

1. THE AudioChoice Android application SHALL derive the Narration_Tier from Account_Access read
   through `GET /v1/account/access`, SHALL treat the Premium_Tier as in effect only while that
   response reports `isActive` as true and reports an `expiresAt` that is absent or later than the
   current instant, and SHALL treat the Free_Tier as in effect otherwise.
2. THE AudioChoice Android application SHALL derive the Narration_Tier from Account_Access alone and
   from no locally recorded purchase state, so that no client decides its own entitlement.
3. THE AudioChoice Android application SHALL read Account_Access at least once every 24 hours while
   the library holds at least one Narrated_Book, and SHALL read Account_Access when a listener opens
   the voice selection surface of a Narrated_Book.
4. WHILE the Narration_Tier is the Free_Tier, THE AudioChoice Android application SHALL offer the
   System_Voice and the Local_Neural_Voice as selectable voices, and SHALL submit no Spoken_Text to
   the AudioChoice backend for synthesis.
5. WHILE the Narration_Tier is the Premium_Tier, THE AudioChoice Android application SHALL offer the
   System_Voice, the Local_Neural_Voice and the Premium_Voice as selectable voices.
6. WHEN the Narration_Tier changes from the Premium_Tier to the Free_Tier while a Narrated_Book
   holds a Chapter_Audio rendered by the Premium_Voice, THE AudioChoice Android application SHALL
   keep every rendered Chapter_Audio of that Narrated_Book playable, SHALL submit no further
   Narration_Unit of that Narrated_Book to the Premium_Voice, SHALL report that the premium voice is
   no longer available for that account, and SHALL offer to render the remaining Narration_Chapters
   with the System_Voice or with the Local_Neural_Voice.
7. WHEN a listener accepts the offer of acceptance criterion 6, THE AudioChoice Android application
   SHALL record the chosen on-device voice as the Selected_Voice, SHALL keep every Chapter_Audio
   already rendered by the Premium_Voice, and SHALL render the remaining Narration_Chapters with the
   chosen on-device voice.
8. WHERE Account_Access cannot be read, THE AudioChoice Android application SHALL keep the last
   recorded Narration_Tier in effect for the Tier_Grace_Period measured from the most recent
   successful read of Account_Access.
9. IF Account_Access has not been read successfully for longer than the Tier_Grace_Period, THEN THE
   AudioChoice Android application SHALL treat the Narration_Tier as the Free_Tier and SHALL report
   that the account's entitlement could not be confirmed.
10. WHEN the AudioChoice Android application reads Account_Access successfully, THE
    AudioChoice Android application SHALL record the derived Narration_Tier, the `plan` value and
    the timestamp of that read.
11. THE AudioChoice backend SHALL grant a Premium_Tier entitlement only through the existing
    administrative grant endpoint `POST /v1/admin/accounts/{userID}/entitlements`, which is
    restricted to the configured administrator token, for the duration of the experimental cycle.
12. THE AudioChoice Android application SHALL present no purchase control and no price for the
    Premium_Tier, because purchase verification is outside the scope of this document.
13. THE AudioChoice product SHALL offer the Premium_Tier for sale only after Google Play Billing
    purchases are verified server-side and recorded through the existing entitlement store, that
    verification being a prerequisite tracked outside this document.

### Requirement 8: Narrate on the device, climbing to a better on-device voice only where the device keeps up

**User Story:** As a listener, I want narration to work without paying anything, without an internet
connection, and without my book leaving my phone, and I want a better on-device voice only when my
phone can actually produce it faster than I listen, so that adding an EPUB is as unremarkable as
adding an audio file.

#### Acceptance Criteria

1. WHEN a listener creates a Narrated_Book, THE AudioChoice Android application SHALL record as the
   Selected_Voice the System_Voice together with the voice identifier that the installed Android
   `TextToSpeech` engine reports as its default voice for the Book_Text_Language, and SHALL require
   no listener choice before rendering begins.
2. THE AudioChoice Android application SHALL present on the voice selection surface of a
   Narrated_Book the voices the installed Android `TextToSpeech` engine reports for the
   Book_Text_Language, and SHALL record the listener's choice among those voices as the
   Selected_Voice for that Narrated_Book.
3. THE System_Voice SHALL synthesize Spoken_Text through the Android `TextToSpeech`
   `synthesizeToFile` interface, SHALL perform no network request, and SHALL complete synthesis
   while the device has no network connectivity.
4. THE Local_Neural_Voice SHALL synthesize Spoken_Text from a neural model held on the device as an
   application asset of 100 MB or less, SHALL perform no network request during synthesis, and
   SHALL send no character of Spoken_Text off the device.
5. WHERE the Local_Neural_Voice model is absent from the device, THE AudioChoice Android application
   SHALL present the model's download size in megabytes and SHALL download that model only after the
   listener confirms.
6. THE AudioChoice Android application SHALL measure the Synthesis_Rate of the Local_Neural_Voice on
   the device before offering the Local_Neural_Voice as selectable, by synthesizing a fixed
   measurement text of between 200 and 400 characters and dividing the duration of the audio
   produced by the wall-clock time taken.
7. WHERE the measured Synthesis_Rate of the Local_Neural_Voice is greater than the product of the
   Playback_Speed_Ceiling and the Synthesis_Rate_Margin, THE AudioChoice Android application SHALL
   offer the Local_Neural_Voice as a selectable voice, so that rendering cannot fall behind
   listening.
8. IF the measured Synthesis_Rate of the Local_Neural_Voice is no greater than the product of the
   Playback_Speed_Ceiling and the Synthesis_Rate_Margin, THEN THE AudioChoice Android application
   SHALL present the Local_Neural_Voice as unavailable on that device, SHALL state that the device
   cannot render narration fast enough to keep up with listening, and SHALL keep the System_Voice as
   the Selected_Voice.
9. THE AudioChoice Android application SHALL record the measured Synthesis_Rate of the
   Local_Neural_Voice against the device, and SHALL measure the Synthesis_Rate again when the
   Local_Neural_Voice model version changes.
10. WHILE a Narrated_Book is playing and a Narration_Chapter of that Narrated_Book is in the
    rendering Render_State, IF the playback position reaches the end of the last Narration_Chapter in
    the rendered Render_State, THEN THE Player SHALL pause playback within 1.0 second and THE
    AudioChoice Android application SHALL report that narration is still rendering, so that the
    Player presents neither silence nor audio from an earlier render.
11. THE System_Voice and the Local_Neural_Voice SHALL synthesize at a fixed rate of 1.0 and a fixed
    pitch of 1.0, so that the Player's speed control remains the single place playback speed is set.
12. IF no Android `TextToSpeech` engine is installed on the device, or if the installed Android
    `TextToSpeech` engine does not report successful initialization within 5.0 seconds of an
    initialization request, THEN THE AudioChoice Android application SHALL report that the device has
    no installed voice, SHALL offer to open the Android text-to-speech settings screen, and SHALL
    keep the Narrated_Book unrendered.
13. IF the Android `TextToSpeech` engine reports no voice for the Book_Text_Language, THEN THE
    AudioChoice Android application SHALL report which language the Source_EPUB declares, SHALL offer
    to open the Android text-to-speech settings screen, and SHALL keep the Narrated_Book unrendered
    until the listener selects a voice among those the engine reports.
14. IF the Local_Neural_Voice model cannot be loaded, or a Local_Neural_Voice synthesis request
    reports an error on three consecutive Narration_Units, THEN THE AudioChoice Android application
    SHALL record the System_Voice as the Selected_Voice, SHALL report that the on-device neural
    voice is unavailable and that narration continues with the system voice, and SHALL keep every
    rendered Chapter_Audio playable.
15. WHEN a listener changes the Selected_Voice for a Narrated_Book, THE AudioChoice Android
    application SHALL present the count of Narration_Chapters in the rendered Render_State and SHALL
    request the listener's confirmation before discarding any Chapter_Audio.
16. WHEN a listener confirms a change of the Selected_Voice for a Narrated_Book, THE
    Narration_Renderer SHALL discard every Chapter_Audio and Chapter_Timeline of that Narrated_Book,
    SHALL set every Narration_Chapter's Render_State to not rendered, and SHALL render that
    Narrated_Book again with the new Selected_Voice under the Render_Ahead_Window.
17. WHEN a listener declines a change of the Selected_Voice for a Narrated_Book, THE AudioChoice
    Android application SHALL keep the previous Selected_Voice and SHALL keep every Chapter_Audio
    and Chapter_Timeline of that Narrated_Book.
18. IF a System_Voice or Local_Neural_Voice synthesis request reports an error or produces no audio
    file within 30.0 seconds, THEN THE Narration_Renderer SHALL retry that request up to two times
    and SHALL record the Narration_Chapter's Render_State as render failed when every attempt fails.
19. IF a Narration_Unit's Spoken_Text exceeds the maximum input length the Selected_Voice's
    Voice_Engine accepts after Pronunciation_Rules have been applied, THEN THE Narration_Renderer
    SHALL submit that Spoken_Text as consecutive synthesis requests split at word boundaries and
    SHALL record one Chapter_Timeline entry whose Source_Range is that Narration_Unit's Source_Range.

### Requirement 9: Offer the premium voice only after saying where the text goes

**User Story:** As a listener who finds the on-device voices too flat for a long novel, I want to
choose the premium voice and understand exactly what selecting it sends off my phone, so that I can
decide for myself rather than have the decision made for me.

#### Acceptance Criteria

1. THE AudioChoice Android application SHALL present the Premium_Voice on the voice selection
   surface of a Narrated_Book WHILE the Narration_Tier is the Premium_Tier, and SHALL keep the
   Premium_Voice unselected until a Premium_Voice_Acknowledgement carrying the current agreement
   version is recorded.
2. WHEN a listener selects the Premium_Voice, THE AudioChoice Android application SHALL present,
   before recording a Premium_Voice_Acknowledgement, a statement that the book's Spoken_Text is sent
   to the AudioChoice backend for synthesis, a statement that synthesis is performed on the
   AudioChoice-owned Synthesis_Endpoint hosted by Amazon SageMaker or by Amazon Polly as the named
   fallback synthesis service, a statement that AudioChoice retains Spoken_Text only until the
   Chapter_Audio for that request is written, a statement that the System_Voice and the
   Local_Neural_Voice remain available and send no text off the device, a control that accepts and a
   control that declines.
3. THE AudioChoice Android application SHALL record a Premium_Voice_Acknowledgement against the
   account, carrying the agreement version, the agreement text and the acceptance timestamp, before
   submitting any Spoken_Text to the AudioChoice backend for Premium_Voice synthesis.
4. THE AudioChoice Android application SHALL submit no Spoken_Text for Premium_Voice synthesis
   WHILE no Premium_Voice_Acknowledgement carrying the current agreement version is recorded.
5. THE AudioChoice Android application SHALL present the Premium_Voice as included in the
   Premium_Tier, and SHALL present for the Premium_Voice no per-book charge and no character count,
   because the Premium_Tier is a subscription rather than a metered service.
6. THE AudioChoice Android application SHALL present a sample of each offered Premium_Voice, between
   3.0 and 30.0 seconds long, before a listener selects that voice, and SHALL submit no Spoken_Text
   of the Narrated_Book to produce that sample.
7. IF a Premium_Voice synthesis request fails or does not complete within 30.0 seconds, THEN THE
   Narration_Renderer SHALL submit that request again at most three times, waiting 2.0 seconds
   before the first resubmission, 4.0 seconds before the second and 8.0 seconds before the third,
   and SHALL record the Narration_Chapter's Render_State as render failed when the fourth attempt
   fails.
8. IF the device has no network connectivity while the Selected_Voice is the Premium_Voice, THEN THE
   Narration_Renderer SHALL pause the Render_Queue within 5.0 seconds, SHALL consume no attempt of
   acceptance criterion 7 while connectivity is absent, SHALL report that rendering continues when
   connectivity returns, SHALL leave every rendered Chapter_Audio playable, and SHALL resume the
   Render_Queue within 10.0 seconds of connectivity returning.
9. THE AudioChoice backend SHALL persist a Premium_Voice_Acknowledgement record carrying the account
   identifier, the agreement version, the agreement text and the acceptance timestamp.
10. WHERE the AudioChoice backend cannot be reached when a listener grants a
    Premium_Voice_Acknowledgement, THE AudioChoice Android application SHALL record that
    acknowledgement locally, SHALL treat that local record as sufficient to submit Spoken_Text for
    Premium_Voice synthesis, SHALL retain that record until the AudioChoice backend confirms it is
    persisted, and SHALL deliver that record to the AudioChoice backend when the backend next
    responds.
11. IF a listener declines the presentation of acceptance criterion 2 or leaves that presentation
    without accepting, THEN THE AudioChoice Android application SHALL leave the Narrated_Book's
    Selected_Voice unchanged, SHALL record no Premium_Voice_Acknowledgement, SHALL discard no
    Chapter_Audio, and SHALL submit no Spoken_Text for Premium_Voice synthesis.
12. IF the agreement version of the recorded Premium_Voice_Acknowledgement differs from the current
    agreement version, THEN THE AudioChoice Android application SHALL present acceptance criterion 2
    again and SHALL submit no further Spoken_Text for Premium_Voice synthesis until a
    Premium_Voice_Acknowledgement carrying the current agreement version is recorded, while leaving
    every rendered Chapter_Audio playable.

### Requirement 10: Synthesize the premium voice on AudioChoice-owned AWS infrastructure

**User Story:** As the operator of AudioChoice, I want premium narration to run on an AWS endpoint I
own with Amazon Polly behind it and none of it on the transcription GPU, so that narration meets its
playback deadline without slowing the scans that filtering depends on.

#### Acceptance Criteria

1. THE AudioChoice backend SHALL expose exactly two Synthesis_Provider implementations behind one
   interface, being the Primary_Synthesis_Provider and the Fallback_Synthesis_Provider, so that the
   narration pipeline selects a provider by configuration in the manner the transcription pipeline
   selects an `ITranscriptionProvider` implementation.
2. THE Primary_Synthesis_Provider SHALL perform synthesis on the Synthesis_Endpoint, that endpoint
   holding a frontier text-to-speech model deployed through Amazon SageMaker JumpStart and being
   billed as Amazon SageMaker instance-hours rather than per character of Spoken_Text.
3. THE Fallback_Synthesis_Provider SHALL perform synthesis through Amazon Polly, and THE
   AudioChoice backend SHALL launch this feature with the Fallback_Synthesis_Provider configured as
   the Synthesis_Provider in effect, because Polly Generative requires no AudioChoice-operated
   infrastructure.
4. THE AudioChoice backend SHALL perform no narration synthesis on the Transcription_GPU_Host, that
   isolation being deliberate so that a narration request carrying a playback deadline never
   competes with transcription throughput for the same GPU.
5. IF the Primary_Synthesis_Provider returns an error, reports that the Synthesis_Endpoint is
   unavailable, or returns no audio within 60 seconds of a synthesis request for one
   Narration_Chapter, THEN THE AudioChoice backend SHALL route that request to the
   Fallback_Synthesis_Provider.
6. IF the Synthesis_Endpoint is scaled to zero instances when a synthesis request arrives and
   returns no audio within the recorded Cold_Start_Delay plus 60 seconds, THEN THE AudioChoice
   backend SHALL route that request to the Fallback_Synthesis_Provider.
7. WHEN the AudioChoice backend produces a Chapter_Audio, THE AudioChoice backend SHALL record which
   Synthesis_Provider produced it and that provider's model version against the Source_EPUB
   fingerprint and the Narration_Chapter index.
8. THE AudioChoice backend SHALL write each Chapter_Audio it produces to the Narration_Object_Store
   and SHALL serve that Chapter_Audio to the AudioChoice Android application from the
   Narration_Object_Store rather than from the Synthesis_Endpoint or from any other synthesis host.
9. THE AudioChoice backend SHALL issue each Chapter_Audio download URL for the requesting account
   only and SHALL expire that URL within 3600 seconds of issuing it.
10. THE AudioChoice backend SHALL encode each Chapter_Audio as single-channel Opus at a target
    bitrate of 32 kbps, that encoding being chosen because the content is speech.
11. THE AudioChoice backend SHALL write no character of Spoken_Text to any persistent store, log
    file, cache, checkpoint or telemetry record, and SHALL retain Spoken_Text only until the
    Chapter_Audio for that synthesis request is written to the Narration_Object_Store.
12. THE AudioChoice backend SHALL measure the Synthesis_Rate of the Primary_Synthesis_Provider on
    the Amazon SageMaker instance type chosen for the Synthesis_Endpoint and SHALL persist that
    value as a Narration_Measurement_Record, that measurement being required before a
    Render_Ahead_Window value is fixed, because the throughput this feature assumes is unverified
    until it is measured.
13. THE AudioChoice backend SHALL measure the Cold_Start_Delay of the Synthesis_Endpoint and SHALL
    persist that value as a Narration_Measurement_Record, that measurement being required before it
    is decided whether the Synthesis_Endpoint scales to zero instances.
14. THE AudioChoice Android application SHALL measure the Synthesis_Rate of the Local_Neural_Voice
    on a Mid_Range_Device and SHALL persist that value as a Narration_Measurement_Record, that
    measurement being required before the Local_Neural_Voice is offered as a selectable voice.
15. THE Render_Ahead_Window value in effect SHALL be derived from the Narration_Measurement_Records
    of acceptance criteria 12 and 14 together with the Playback_Speed_Ceiling, and THE AudioChoice
    backend SHALL record the Render_Ahead_Window value in effect alongside the
    Narration_Measurement_Record it was derived from.
16. THE selection between the Primary_Synthesis_Provider and the Fallback_Synthesis_Provider for the
    Premium_Voice SHALL be decided by rendering one Reference_Chapter in full with each provider and
    comparing those two renderings, and SHALL be decided by no comparison of samples shorter than a
    Reference_Chapter, because a short sample exercises neither long-form pacing consistency nor
    dialogue handling.
17. THE selected Premium_Voice Synthesis_Provider SHALL be verified as covered by the
    AWS_Billing_Arrangement before that provider is deployed, that verification covering any
    third-party model software charge that Amazon SageMaker JumpStart levies separately from Amazon
    SageMaker infrastructure charges.
18. IF the Primary_Synthesis_Provider is not covered by the AWS_Billing_Arrangement, THEN THE
    AudioChoice backend SHALL configure the Fallback_Synthesis_Provider as the Synthesis_Provider in
    effect for the Premium_Voice and SHALL deploy no Synthesis_Endpoint.
19. WHEN the AudioChoice Android application receives a Chapter_Audio download URL, THE
    AudioChoice Android application SHALL download that Chapter_Audio to app-private storage and
    SHALL treat it as the Chapter_Audio of that Narration_Chapter for every purpose this document
    states.

### Requirement 11: Render ahead of the playhead rather than rendering the whole book

**User Story:** As a listener who has just added an EPUB, I want to start listening within minutes
and to have the next few chapters ready without my phone rendering all night or my premium
allowance producing chapters I never reach, so that adding a book costs only what listening to it
needs.

#### Acceptance Criteria

1. THE Narration_Renderer SHALL render Narration_Chapters in the order the Narration_Plan records.
2. WHEN a Narration_Chapter is the first Narration_Chapter of a Narrated_Book to reach the rendered
   Render_State, THE AudioChoice Android application SHALL make the Narrated_Book playable from that
   Narration_Chapter within 2.0 seconds and SHALL present that Narration_Chapters remain to be
   rendered.
3. WHILE a Narrated_Book holds fewer Narration_Chapters in the rendered Render_State after the
   Narration_Chapter containing the current playback position than the Render_Ahead_Window states,
   and no listener pause request is in effect for that Narrated_Book, THE Narration_Renderer SHALL
   render the earliest Narration_Chapter in the not-rendered Render_State at or after that playback
   position through an Android `WorkManager` worker, so that rendering continues while the
   AudioChoice Android application is in the background.
4. WHEN the count of Narration_Chapters in the rendered Render_State after the Narration_Chapter
   containing the current playback position reaches the Render_Ahead_Window, THE Narration_Renderer
   SHALL stop rendering within 5.0 seconds and SHALL start no further Narration_Chapter until that
   count falls below the Render_Ahead_Window or a Full_Book_Render_Request is in effect, so that
   rendering bounds both premium synthesis cost and on-device battery and CPU time.
5. WHEN the playback position of a Narrated_Book advances into a Narration_Chapter later in
   Narration_Plan order than the Narration_Chapter it was in, THE Narration_Renderer SHALL begin
   rendering, within 5.0 seconds, the earliest Narration_Chapter in the not-rendered Render_State
   required to satisfy the Render_Ahead_Window again.
6. THE AudioChoice Android application SHALL present, on the Narrated_Book detail surface, a control
   that records a Full_Book_Render_Request for that Narrated_Book.
7. WHILE a Full_Book_Render_Request is in effect for a Narrated_Book and no listener pause request
   is in effect for that Narrated_Book, THE Narration_Renderer SHALL render every Narration_Chapter
   of that Narrated_Book in Narration_Plan order regardless of the Render_Ahead_Window.
8. WHEN a listener activates the control of acceptance criterion 6, THE AudioChoice Android
   application SHALL present the count of Narration_Chapters that will be rendered, the estimated
   storage in megabytes those Narration_Chapters will occupy, and, WHERE the Selected_Voice is the
   Premium_Voice, that every remaining Narration_Chapter will be synthesized through a
   Synthesis_Provider, and SHALL record the Full_Book_Render_Request only after the listener
   confirms.
9. WHILE a Narrated_Book is rendering, THE AudioChoice Android application SHALL present the count
   of rendered Narration_Chapters, the count of Narration_Chapters in the render failed
   Render_State, the total count of Narration_Chapters, and the title of the Narration_Chapter
   currently rendering, and SHALL update those values at least once every 2.0 seconds.
10. WHILE a Narrated_Book is rendering, THE Narration_Renderer SHALL display a foreground
    notification naming the Narrated_Book, the count of rendered Narration_Chapters and the total
    count of Narration_Chapters, and SHALL remove that notification within 5.0 seconds of rendering
    stopping.
11. WHEN playback of a Narrated_Book reaches the end of the last rendered Narration_Chapter while a
    Narration_Chapter in the not-rendered or rendering Render_State remains, THE Player SHALL pause
    within 1.0 second, SHALL keep the playback position at the end of that rendered
    Narration_Chapter, and THE AudioChoice Android application SHALL report that the next chapter is
    still rendering.
12. WHEN a Narration_Chapter reaches the rendered Render_State while playback is paused at the end
    of the previous Narration_Chapter by acceptance criterion 11, THE Player SHALL extend the
    Narration_Timeline to include the newly rendered Narration_Chapter within 2.0 seconds and SHALL
    resume playback from the kept playback position, unless the listener has since sought to another
    position, paused playback, or opened another book.
13. WHEN the Narration_Renderer stops rendering a Narration_Chapter before that Narration_Chapter
    reaches the rendered Render_State for any reason other than render failure, including
    application process termination, worker cancellation and a listener pause request, THE
    Narration_Renderer SHALL discard that Narration_Chapter's partial Chapter_Audio, SHALL set that
    Narration_Chapter's Render_State to not rendered, and SHALL render that Narration_Chapter again
    from its first Narration_Unit when rendering next starts.
14. IF rendering a Narration_Chapter fails, THEN THE AudioChoice Android application SHALL record
    that Narration_Chapter's Render_State as render failed, SHALL report the failure naming that
    Narration_Chapter's title, SHALL offer to render that Narration_Chapter again, SHALL keep every
    rendered Chapter_Audio playable, and SHALL start rendering the next Narration_Chapter that the
    Render_Ahead_Window or a Full_Book_Render_Request requires within 5.0 seconds.
15. THE Narration_Renderer SHALL render at most one Narration_Chapter at a time for one
    Narrated_Book.
16. WHEN a listener requests that rendering pause, THE Narration_Renderer SHALL stop within
    5.0 seconds, SHALL keep every rendered Chapter_Audio and Chapter_Timeline, SHALL keep the
    Render_Queue and each Narration_Chapter's Render_State, and SHALL start no further
    Narration_Chapter until the listener requests that rendering resume.
17. WHEN a listener opens a Narrated_Book whose rendered Narration_Chapters after the playback
    position number fewer than the Render_Ahead_Window and for which no listener pause request is in
    effect, THE Narration_Renderer SHALL resume rendering within 5.0 seconds from the earliest
    Narration_Chapter in the not-rendered Render_State at or after that playback position, without
    rendering an already-rendered Narration_Chapter again.
18. WHEN the Narration_Store persists a Narration_Plan for a Narrated_Book whose Render_Queue holds
    a Narration_Chapter in the not-rendered Render_State, THE Narration_Renderer SHALL start
    rendering the first Narration_Chapter of that Narration_Plan within 5.0 seconds.
19. THE Narration_Renderer SHALL bring the first Narration_Chapter of a Narrated_Book to the
    rendered Render_State within 300 seconds of rendering starting, for a first Narration_Chapter
    whose Narration_Units hold 20,000 characters of Spoken_Text or fewer, rendered by the
    System_Voice on a device with four or more CPU cores.
20. IF every Narration_Chapter of a Narrated_Book is in the render failed Render_State, THEN THE
    AudioChoice Android application SHALL report that the Narrated_Book could not be narrated with
    the Selected_Voice, SHALL offer to render the Narration_Plan again, SHALL offer to change the
    Selected_Voice, and SHALL keep the Narration_Plan and the Text_Scan_Events.
21. THE Render_Ahead_Window SHALL hold a value of at least 1 Narration_Chapter derived from the
    Synthesis_Rate that Requirement 10 requires to be measured for the Selected_Voice's Voice_Engine
    and from the Playback_Speed_Ceiling, and SHALL hold no value fixed before that Synthesis_Rate is
    measured.

### Requirement 12: Play a narrated book the way an audiobook plays

**User Story:** As a listener, I want a synthesized book to behave like every other book in my
library, so that I do not have to learn a second set of controls for it.

#### Acceptance Criteria

1. WHEN a listener opens a Narrated_Book that holds at least one Narration_Chapter in the rendered
   Render_State, THE Player SHALL load that Narrated_Book's rendered Chapter_Audio files, in
   Narration_Plan order, as one ordered playlist through the existing `AudioChoicePlaybackService`
   media session, and SHALL position playback at the Book_Time recorded through the existing
   progress path, or at 0.0 seconds when no position is recorded for that Narrated_Book.
2. THE Player SHALL report a Narrated_Book's position in Book_Time and its duration as the
   Narration_Duration of the Narration_Chapters currently in the rendered Render_State, and SHALL
   report a changed duration within 2.0 seconds of a Narration_Chapter entering or leaving the
   rendered Render_State, so that a Narrated_Book's position is measured across Narration_Chapters
   rather than within one Chapter_Audio.
3. THE Player SHALL apply the per-book playback speed stored for a Narrated_Book by the same path it
   applies the speed of an Imported_Audiobook.
4. THE Player SHALL accept a seek of a Narrated_Book to any Book_Time from 0.0 seconds to the
   Narration_Duration inclusive, SHALL clamp a requested Book_Time below 0.0 seconds to
   0.0 seconds, and SHALL resume from a Book_Time within 0.25 seconds of the requested Book_Time,
   including where the requested Book_Time falls in a different Chapter_Audio than the position the
   seek started from.
5. THE Player SHALL apply the chapter forward and chapter back controls of a Narrated_Book by
   seeking to the Book_Time at which the next or the previous rendered Narration_Chapter begins, and
   SHALL apply the seek forward and seek back controls by moving the position in Book_Time by the
   seek interval the listener has configured for the existing Player controls.
6. WHILE a Narrated_Book is playing, THE Player SHALL record that Narrated_Book's playback position
   in Book_Time through the existing progress path at least once every 5.0 seconds, and SHALL record
   that position when playback pauses and when playback stops, so that a Narrated_Book resumes where
   the listener stopped.
7. THE Player SHALL support bookmarks for a Narrated_Book at Book_Time positions through the
   existing bookmark path.
8. WHEN the sleep timer expires while a Narrated_Book is playing, THE Player SHALL pause playback
   within 1.0 second of the expiry by the same path it pauses an Imported_Audiobook, and SHALL
   record the Narrated_Book's playback position in Book_Time through the existing progress path.
9. THE Player SHALL present the Narrated_Book title, author and cover in the Android media
   notification and on the lock screen.
10. IF a listener seeks a Narrated_Book to a Book_Time greater than the Narration_Duration or to a
    Book_Time falling within a Narration_Chapter whose Render_State is not rendered, THEN THE Player
    SHALL position playback at the end of the last rendered Narration_Chapter, SHALL pause playback,
    and SHALL report that the requested position is not yet rendered.
11. WHEN playback of a Narrated_Book whose every Narration_Chapter is in the rendered Render_State
    reaches a Book_Time within 1.0 second of the Narration_Duration, THE Player SHALL mark that
    Narrated_Book finished through the existing progress path.
12. FOR ALL Narration_Timelines, a character offset converted to Book_Time by
    `readerTimeForCharacter` and converted back by `readerCharacterForTime` SHALL fall within the
    Source_Range of the Narration_Unit that contains the original offset (round-trip property).
13. THE Narration_Timeline SHALL hold `ReaderTimingRange` values ordered by both start time and
    start character offset.
14. IF a listener opens a Narrated_Book that holds no Narration_Chapter in the rendered
    Render_State, THEN THE Player SHALL load no playlist, SHALL keep playback stopped, and SHALL
    report that the Narrated_Book has no rendered narration to play yet.
15. IF the Player cannot read the Chapter_Audio of the Narration_Chapter it is about to play, THEN
    THE Player SHALL pause playback at the Book_Time at which that Narration_Chapter begins, SHALL
    retain the recorded playback position, and SHALL report that the Narration_Chapter must be
    rendered again.
16. IF playback of a Narrated_Book reaches the end of the last rendered Narration_Chapter while any
    Narration_Chapter is in a Render_State other than rendered, THEN THE Player SHALL leave that
    Narrated_Book not finished and SHALL retain the recorded playback position.

### Requirement 13: Follow the text while the narration plays

**User Story:** As a listener of a synthesized book, I want the text to scroll and highlight in step
with the voice and to be able to tap a paragraph to jump there, so that I can move between reading
and listening in the same book.

#### Acceptance Criteria

1. WHEN a listener opens the Reader for a Narrated_Book, THE Reader SHALL render, within
   2.0 seconds, the paragraphs `ReaderParagraphParser.parse` produces from Book_Text.
2. WHILE a Narrated_Book is playing and the reader follow-audio setting is enabled, THE Reader SHALL
   highlight exactly one paragraph, the paragraph whose Source_Range contains the character offset
   that `readerCharacterForTime` returns for the current Book_Time, and SHALL update that highlight
   at least once every 500 milliseconds.
3. WHEN a listener taps a paragraph in the Reader of a Narrated_Book, THE Player SHALL seek, within
   1.0 second of the tap, to the Book_Time that `readerTimeForCharacter` returns for the first
   character offset of that paragraph that a `ReaderTimingRange` of the Narration_Timeline covers.
4. THE Reader SHALL remove every Filtered_Range from the text of a Narrated_Book through
   `readerDisplayParagraphs`, and SHALL render no paragraph whose characters Filtered_Ranges cover
   in full, so that the reader displays the same content the narration speaks.
5. THE Narration_Timeline of a Narrated_Book SHALL hold exactly one `ReaderTimingRange` for every
   Narration_Unit of a Narration_Chapter in the rendered Render_State whose Source_Range no
   Filtered_Range covers in full, so that coverage of a Narrated_Book's prose is complete rather
   than sparse.
6. THE Reader SHALL record and restore a Narrated_Book's reading position through the existing
   reader position path.
7. THE Reader SHALL apply the device-wide `ReaderSettings` to a Narrated_Book without change.
8. WHERE a character offset falls in a Non_Prose_Block, THE Reader SHALL render that offset's text
   and SHALL apply no narration highlight to that text.
9. WHILE the reader follow-audio setting is enabled, WHEN the paragraph the Reader highlights
   changes, THE Reader SHALL bring that paragraph fully into the visible text area within
   500 milliseconds of the change.
10. IF a listener taps a paragraph that contains no character offset covered by a
    `ReaderTimingRange` of the Narration_Timeline, THEN THE Player SHALL keep its current playback
    position unchanged and THE Reader SHALL report that the tapped text has no narration yet.
11. IF `readerCharacterForTime` returns no character offset for the current Book_Time, THEN THE
    Reader SHALL keep the paragraph it highlighted last highlighted and SHALL change the visible
    text area by no scroll of its own.

### Requirement 14: Correct words the voice gets wrong

**User Story:** As a listener of a fantasy novel full of invented names, I want to fix a
mispronunciation once and have it hold for the rest of the book, so that a mangled character name
does not follow me through forty hours of narration.

#### Acceptance Criteria

1. THE AudioChoice Android application SHALL present a control that records a Pronunciation_Rule
   from a written form of 1 to 100 characters and a replacement form of 1 to 100 characters, each
   measured after leading and trailing whitespace is removed, and SHALL record for each
   Pronunciation_Rule whether its scope is one Narrated_Book or the account.
2. THE Narration_Renderer SHALL apply every Pronunciation_Rule scoped to the Narrated_Book and every
   Pronunciation_Rule scoped to the account to the characters that remain after Filtered_Ranges have
   been excluded, before submitting Spoken_Text to a Voice_Engine, and SHALL match no written form
   across the boundary of an excluded Filtered_Range.
3. THE Narration_Renderer SHALL apply no Pronunciation_Rule to Book_Text, so that character offsets,
   the Reader and Filtered_Ranges are unaffected by a Pronunciation_Rule.
4. THE Narration_Store SHALL persist Pronunciation_Rules against the Source_EPUB SHA-256, each
   carrying its written form, its replacement form and its position in the recording order, and
   SHALL keep the Narration_Plan and every Narration_Unit Source_Range unchanged when a
   Pronunciation_Rule is recorded, edited or deleted.
5. THE Narration_Store SHALL persist Pronunciation_Rules that a listener marks as applying to every
   Narrated_Book against the account rather than against one Source_EPUB, and SHALL apply those
   Pronunciation_Rules to every Narrated_Book of that account.
6. WHEN a listener records, edits or deletes a Pronunciation_Rule, THE AudioChoice Android
   application SHALL present the count of Narration_Chapters that are in the rendered Render_State
   and that contain at least one match of the rule's written form under the matching stated in
   acceptance criterion 7, SHALL offer to render those Narration_Chapters again, and SHALL discard
   no Chapter_Audio until the listener accepts that offer.
7. THE Narration_Renderer SHALL match a Pronunciation_Rule's written form only where the character
   preceding the match and the character following the match are each absent or are neither a letter
   nor a digit, and SHALL match without regard to letter case.
8. THE Narration_Renderer SHALL apply Pronunciation_Rules to Spoken_Text in one pass in ascending
   character offset order, SHALL apply at most one Pronunciation_Rule to any character of
   Spoken_Text, SHALL apply no Pronunciation_Rule to the characters of a replacement form it has
   already substituted, and SHALL, where two or more Pronunciation_Rules match at the same character
   offset, apply the Pronunciation_Rule scoped to the Narrated_Book ahead of any Pronunciation_Rule
   scoped to the account and apply the earlier-recorded Pronunciation_Rule when both share one
   scope.
9. WHERE the Selected_Voice is available for synthesis, WHEN a listener requests a preview of a
   Pronunciation_Rule, THE AudioChoice Android application SHALL speak the replacement form using
   the Selected_Voice, SHALL begin speaking within 3.0 seconds of the request, and SHALL speak for
   no longer than 10 seconds.
10. IF a listener submits a Pronunciation_Rule whose written form or whose replacement form is empty
    after leading and trailing whitespace is removed, or is longer than 100 characters, THEN THE
    AudioChoice Android application SHALL record no Pronunciation_Rule, SHALL present an error
    indication naming which form is out of bounds, and SHALL retain the values the listener entered.
11. IF a listener submits a Pronunciation_Rule whose written form matches, without regard to letter
    case, the written form of an existing Pronunciation_Rule of the same scope, THEN THE AudioChoice
    Android application SHALL record no second Pronunciation_Rule, SHALL present an indication that
    a Pronunciation_Rule for that written form already exists, and SHALL offer to edit the existing
    Pronunciation_Rule.
12. IF a listener submits a Pronunciation_Rule when 200 Pronunciation_Rules are already persisted
    for that scope, THEN THE AudioChoice Android application SHALL record no Pronunciation_Rule,
    SHALL present an indication that the limit of 200 Pronunciation_Rules for that scope is reached,
    and SHALL leave every persisted Pronunciation_Rule unchanged.

### Requirement 15: Re-render when filter choices change

**User Story:** As a listener who turns a filter off partway through a book, I want to understand
that the affected chapters need to be produced again and how long that will take, so that a filter
change does not silently leave my book inconsistent.

#### Acceptance Criteria

1. WHEN a listener changes a filter choice for a Narrated_Book, THE AudioChoice Android application
   SHALL identify, within 2.0 seconds, every Narration_Chapter whose Render_State is rendered or
   rendering and whose Source_Range overlaps a Text_Scan_Event whose enabled state, as reported by
   `PlaybackFilterPredicate.isEnabled`, differs between the previous filter choices and the changed
   filter choices.
2. WHEN a listener changes a filter choice for a Narrated_Book, THE AudioChoice Android application
   SHALL present the count of Narration_Chapters identified by acceptance criterion 1 and an
   estimate, expressed in whole minutes, of the time to render those Narration_Chapters again
   derived from the character count of their Narration_Units, SHALL request the listener's
   confirmation, and SHALL discard no Chapter_Audio and write no changed filter choice through
   `BookFilterSettings` until the listener confirms.
3. WHEN a listener confirms a filter change for a Narrated_Book, THE Narration_Renderer SHALL
   discard the Chapter_Audio and Chapter_Timeline of every identified Narration_Chapter, SHALL set
   each identified Narration_Chapter's Render_State to not rendered, and SHALL place every
   identified Narration_Chapter in the Render_Queue in the order the Narration_Plan records.
4. WHEN a listener declines a filter change for a Narrated_Book, THE AudioChoice Android application
   SHALL restore the previous filter choice for that Narrated_Book through `BookFilterSettings`,
   SHALL keep every Chapter_Audio and every Chapter_Timeline, and SHALL leave every
   Narration_Chapter's Render_State unchanged.
5. WHERE the current playback position lies within an identified Narration_Chapter, WHEN a listener
   confirms a filter change for a Narrated_Book, THE Narration_Renderer SHALL render that
   Narration_Chapter before rendering any other identified Narration_Chapter.
6. WHILE an identified Narration_Chapter is being rendered again and the current playback position
   lies within a Narration_Chapter that acceptance criterion 1 did not identify, THE Player SHALL
   continue playing without interruption.
7. WHERE the Selected_Voice is the Premium_Voice, WHEN a listener changes a filter choice for a
   Narrated_Book, THE AudioChoice Android application SHALL present the count of Narration_Chapters
   that will be synthesized again through a Synthesis_Provider, and SHALL discard no Chapter_Audio
   until the listener confirms that count.
8. WHEN a listener confirms a filter change for a Narrated_Book, THE AudioChoice Android application
   SHALL record the Book_Text character offset that `readerCharacterForTime` returns for the
   playback position at the moment of confirmation, and SHALL restore the playback position to the
   Book_Time that `readerTimeForCharacter` returns for that character offset once every identified
   Narration_Chapter preceding that character offset is in the rendered Render_State.
9. IF a filter change identifies no Narration_Chapter under acceptance criterion 1, THEN THE
   AudioChoice Android application SHALL write the changed filter choice through
   `BookFilterSettings` without requesting confirmation and SHALL discard no Chapter_Audio.
10. IF the current playback position lies within an identified Narration_Chapter when a listener
    confirms a filter change, THEN THE Player SHALL pause and THE AudioChoice Android application
    SHALL report that the chapter at the playback position is being rendered again.
11. IF an identified Narration_Chapter is in the rendering Render_State when a listener confirms a
    filter change, THEN THE Narration_Renderer SHALL stop that rendering within 5.0 seconds and
    SHALL discard that Narration_Chapter's partial Chapter_Audio.

### Requirement 16: Keep narration audio within the storage the device has

**User Story:** As a listener with a phone that is nearly full, I want to know what narration will
cost me in storage and be able to reclaim it, so that adding books does not quietly fill my device.

#### Acceptance Criteria

1. WHEN a listener creates a Narrated_Book, THE AudioChoice Android application SHALL present,
   before rendering begins, an estimate expressed in megabytes of the storage the rendered
   Chapter_Audio will occupy, derived from the total Spoken_Text character count of the Render_Queue
   and the Selected_Voice, and that estimate SHALL fall within 30 percent of the storage the
   Chapter_Audio occupies once every Narration_Chapter of that Narrated_Book reaches the rendered
   Render_State.
2. IF the estimate in acceptance criterion 1 exceeds the free storage of the volume holding
   app-private storage less the Storage_Reserve, THEN THE AudioChoice Android application SHALL
   report that the device has insufficient storage, SHALL state in megabytes the additional free
   storage required, SHALL keep every Narration_Chapter of the Narrated_Book in the not-rendered
   Render_State, and SHALL keep the Narration_Plan and the Text_Scan_Events.
3. WHILE a Narrated_Book is rendering, THE Narration_Renderer SHALL measure the free storage of the
   volume holding app-private storage before starting each Narration_Chapter and at least once every
   30.0 seconds during a Narration_Chapter.
4. THE Narration_Store SHALL write Chapter_Audio to app-private storage.
5. THE AudioChoice Android application SHALL present, for each Narrated_Book, the storage its
   Chapter_Audio occupies expressed in megabytes and equal to the total byte count of that
   Narrated_Book's Chapter_Audio files, and SHALL update that value within 5.0 seconds of a
   Chapter_Audio being written or deleted.
6. THE AudioChoice Android application SHALL present a control that discards every Chapter_Audio of
   a Narrated_Book while keeping the Narration_Plan, the Chapter_Timelines, the Text_Scan_Events,
   the Pronunciation_Rules and the playback position.
7. WHEN a listener deletes a Narrated_Book, THE Narration_Store SHALL delete that Narrated_Book's
   Chapter_Audio, Narration_Plan, Chapter_Timelines, Render_Queue, Selected_Voice, Book_Text cache,
   cover image, Text_Scan_Events and the Pronunciation_Rules recorded against that Source_EPUB,
   SHALL keep the Pronunciation_Rules recorded against the account, and SHALL release the read
   permission held on that Source_EPUB's content URI.
8. WHERE a listener enables narration audio eviction for a Narrated_Book, WHEN playback of that
   Narrated_Book passes the last character offset of a Narration_Chapter, THE Narration_Store SHALL
   delete the Chapter_Audio of every Narration_Chapter of that Narrated_Book that ends more than
   2 Narration_Chapters before the Narration_Chapter containing the current playback position and
   that holds no bookmark.
9. THE AudioChoice Android application SHALL keep narration audio eviction disabled by default.
10. WHEN the Narration_Store deletes a Chapter_Audio, THE Narration_Store SHALL set that
    Narration_Chapter's Render_State to not rendered and SHALL keep that Narration_Chapter's
    Chapter_Timeline.
11. THE existing orphaned-audio purge SHALL treat a Chapter_Audio referenced by a Render_Queue as
    referenced, so that purging reclaims no Chapter_Audio of a Narrated_Book in the library.
12. IF the free storage measured by acceptance criterion 3 falls to or below the Storage_Reserve,
    THEN THE Narration_Renderer SHALL stop rendering within 5.0 seconds, SHALL discard the partial
    Chapter_Audio of the Narration_Chapter that was rendering, SHALL keep every Chapter_Audio
    already in the rendered Render_State, SHALL keep the Render_Queue, and SHALL report that
    rendering stopped because the device is low on storage.
13. WHEN a listener activates the control in acceptance criterion 6, THE AudioChoice Android
    application SHALL present the storage in megabytes that discarding will reclaim and the count of
    Narration_Chapters that will require rendering again, and SHALL discard no Chapter_Audio until
    the listener confirms.
14. IF the Narration_Store cannot delete one of the values named in acceptance criterion 7, THEN THE
    Narration_Store SHALL delete the remaining named values, SHALL keep the Narrated_Book absent
    from the library, and SHALL report that some narration data for that Narrated_Book could not be
    removed.

### Requirement 17: Report when the filter got a narrated book wrong

**User Story:** As a listener who heard something a filter should have caught in a synthesized book,
I want to report that moment, so that the text scanner improves for everyone who narrates that
book.

#### Acceptance Criteria

1. WHILE a Narrated_Book is playing, THE AudioChoice Android application SHALL present the existing
   filter report control, and SHALL take the Player's current Book_Time at the moment the listener
   activates that control as the reported Book_Time.
2. WHEN a listener reports missed content in a Narrated_Book, THE AudioChoice Android application
   SHALL send a `FilterReportRequest` whose `fingerprint` is the Source_EPUB fingerprint and whose
   `positionSeconds` is the character offset that `readerCharacterForTime` returns for the reported
   Book_Time, that offset being an integer from 0 to the Book_Text character count less 1.
3. WHEN a listener reports wrongly filtered content in a Narrated_Book, THE AudioChoice Android
   application SHALL send a `FilterReportRequest` carrying the `scanEventID` and `categoryID` of the
   Enabled_Text_Scan_Event whose Source_Range contains the character offset that
   `readerCharacterForTime` returns for the reported Book_Time, and SHALL select the
   Enabled_Text_Scan_Event with the lowest start character offset when more than one such event
   contains that offset.
4. THE AudioChoice Android application SHALL include no Book_Text, no Spoken_Text and no narration
   audio in a `FilterReportRequest`.
5. THE AudioChoice Android application SHALL record in a `FilterReportRequest` for a Narrated_Book
   that `positionSeconds` carries a character offset into Book_Text rather than a time in seconds,
   so that triage reads a narrated report in the correct coordinate space.
6. IF the AudioChoice backend cannot be reached or does not respond to a Narrated_Book filter report
   within 10.0 seconds, THEN THE AudioChoice Android application SHALL queue that report through the
   existing filter report queue, SHALL deliver each queued report exactly once when the backend next
   responds, and SHALL retain at most 100 queued reports by discarding the oldest queued report.
7. IF no `ReaderTimingRange` of the Narration_Timeline contains the reported Book_Time, THEN THE
   AudioChoice Android application SHALL send no `FilterReportRequest` and SHALL report that the
   reported position maps to no position in the book text.
8. IF a listener reports wrongly filtered content and no Enabled_Text_Scan_Event contains the
   character offset for the reported Book_Time, THEN THE AudioChoice Android application SHALL send
   no `FilterReportRequest`, SHALL report that no filtered passage covers the reported position, and
   SHALL leave the Narrated_Book's filter choices unchanged.
9. WHEN the AudioChoice backend accepts a Narrated_Book filter report, THE AudioChoice Android
   application SHALL present, within 2.0 seconds, an indication that the report was received.

### Requirement 18: Tell narrated books apart in the library

**User Story:** As a listener with both audiobooks and synthesized books, I want to see at a glance
which is which and how far along a synthesized book is, so that I know what I am about to open.

#### Acceptance Criteria

1. THE AudioChoice Android application SHALL present, on each Narrated_Book in every library list
   and on the Narrated_Book detail surface, an indication that the book's narration is synthesized,
   and SHALL present no such indication on an Imported_Audiobook.
2. THE AudioChoice Android application SHALL present, on each Narrated_Book in the library that
   holds a Narration_Chapter in the not-rendered Render_State, the count of Narration_Chapters in
   the rendered Render_State and the total count of Narration_Chapters, and SHALL update both counts
   within 2.0 seconds of a Narration_Chapter entering the rendered Render_State.
3. THE AudioChoice Android application SHALL present, on the Narrated_Book detail surface, the
   Selected_Voice name and whether the Selected_Voice is the System_Voice, the Local_Neural_Voice or
   the Premium_Voice.
4. IF every Narration_Chapter of a Narrated_Book is in the rendered Render_State, THEN THE
   AudioChoice Android application SHALL present that Narrated_Book's Narration_Duration as the
   book's duration, expressed in hours and minutes.
5. THE AudioChoice Android application SHALL present the library as two tabs, one listing
   Imported_Audiobooks and one listing Narrated_Books, SHALL sort and filter within each tab using
   the same sort keys and the same filter controls as the other, and SHALL order a Narrated_Book
   whose Narration_Duration is absent after every book whose duration is present when a list is
   ordered by duration.

   > **Decision changed, 2026-08-29, at the product owner's direction.** This criterion previously
   > required one combined list, on the reasoning that a second library is a second thing to
   > maintain. Two tabs replace it, and the reasoning that overturned it is better: opening a
   > Narrated_Book presents the reader rather than the player, so the two kinds of book behave
   > differently once opened and a combined list would have promised otherwise. The sort and filter
   > controls stay shared, which is what the original criterion was really protecting.
   >
   > A book that is an Imported_Audiobook with an attached EPUB appears in the audiobook tab only,
   > never in both: attaching an EPUB to a recording is read-along, and creates no Narrated_Book
   > (R1.7).
6. THE AudioChoice backend SHALL record a Narrated_Book as a library book whose fingerprint
   `fileType` is `epub` and whose fingerprint `duration` is absent, so that the library list,
   favourites and progress synchronisation apply to a Narrated_Book without change.
7. THE AudioChoice Android application SHALL exclude Narrated_Books from Explore catalogue
   publication, so that the Explore catalogue offers scanned editions of published recordings only.
8. WHILE a Narrated_Book holds a Narration_Chapter that is not in the rendered Render_State, THE
   AudioChoice Android application SHALL present the Narration_Duration of that Narrated_Book's
   rendered Narration_Chapters together with an indication that the duration covers rendered
   chapters only.
9. IF a Narrated_Book holds a Narration_Chapter in the render-failed Render_State, THEN THE
   AudioChoice Android application SHALL present, on that Narrated_Book in the library, an
   indication that rendering failed and the count of Narration_Chapters in the render-failed
   Render_State.
10. IF a Narrated_Book's Selected_Voice identifier matches no voice the Selected_Voice's
    Voice_Engine reports as available, THEN THE AudioChoice Android application SHALL present on the
    Narrated_Book detail surface an indication that the recorded voice is unavailable and SHALL
    offer the voice selection control.

### Requirement 19: Ship EPUB narration in experimental builds only

**User Story:** As a listener on the beta or release channel, I want my app unchanged by a feature
that is still being proven, so that narration work carries no risk to the audiobooks I already
listen to.

#### Acceptance Criteria

1. THE AudioChoice Android application SHALL satisfy every requirement of this document in an
   Experimental_Build.
2. WHILE `BuildConfig.EXPERIMENTAL_BUILD` is false, THE AudioChoice Android application SHALL
   present no EPUB narration import action, SHALL present no narration surface, and SHALL create no
   Narrated_Book.
3. THE beta build and the release build SHALL present the same surfaces, request the same Android
   permissions, and apply the same playback, filtering and reader behaviour to an Imported_Audiobook
   as they do before this feature.
4. THE AudioChoice Android application SHALL gate EPUB narration through the `experimental` build
   type already declared in `android-app/app/build.gradle.kts`, which is created with
   `initWith(getByName("beta"))`, and SHALL introduce no additional build type and no additional
   product flavour.
5. WHEN EPUB narration first ships, THE `experimental` build type SHALL advance the experimental
   cycle identifier it publishes through its `BETA_VERSION` build configuration field.
6. THE Narration_Store SHALL hold every value it persists for a Narrated_Book in the private storage
   of the `experimental` application identifier, so that a beta or release install on the same
   device presents no Narrated_Book and leaves that data unchanged.
7. THE AudioChoice backend SHALL leave the request and response shape of every existing scan,
   library, filter report and account endpoint unchanged, so that a beta or release client is
   unaffected by the narration endpoints this document adds.
