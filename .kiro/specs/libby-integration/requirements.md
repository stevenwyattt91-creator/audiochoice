# Requirements Document

## Introduction

AudioChoice filters audiobook content by skipping spans that a server-side scan flagged for an
edition. Today that only works for audiobooks the listener imported as files, because the app's
own ExoPlayer instance owns the audio.

Libby loans cannot be imported. OverDrive retired the DRM-free MP3 audiobook format on
13 November 2024, and audiobook fulfillment now redirects to an OverDrive-hosted player rather
than returning audio to the calling application. OverDrive also restricts API access to
organizations affiliated with its partner libraries. There is therefore no sanctioned path for
AudioChoice to obtain, decode, or import the audio of a Libby loan.

This feature takes the only remaining approach: AudioChoice acts as an external controller of
Libby's own playback. Libby publishes an Android media session (it supports Android Auto), so
AudioChoice can observe what is playing and where, and can silence or skip past a flagged span
without ever touching the protected audio. Libby remains the player; AudioChoice becomes the
filter that sits over it.

This is Companion Filtering. It requires no OverDrive API access and no library-card credentials,
because it never asks OverDrive for anything. It does require the listener to grant AudioChoice
notification access, and it only works for editions AudioChoice has already scanned.

Scope is the Android app. iOS provides no equivalent cross-application media session control, so
the iOS client is excluded.

## Glossary

- **Companion_Filtering**: The mode in which AudioChoice applies its content filter to audio
  played by a separate application rather than by its own player.
- **External_Session**: An Android media session published by an application other than
  AudioChoice, observed through `MediaSessionManager` while notification access is granted.
- **Libby_Session**: An External_Session whose owning package is Libby
  (`com.overdrive.mobile.android.libby`).
- **Supported_Player**: An application whose External_Session AudioChoice is permitted to observe
  and issue transport commands to. For this feature the only Supported_Player is Libby
  (`com.overdrive.mobile.android.libby`).
- **Companion_Filter_Service**: The AudioChoice Android foreground service that owns
  Companion_Filtering for the duration of one listening session.
- **Session_Observer**: The AudioChoice component that reads metadata and playback state from a
  Libby_Session and derives Book_Position.
- **Edition_Matcher**: The AudioChoice component that decides which Scanned_Edition, if any, the
  audio in a Libby_Session corresponds to.
- **Scanned_Edition**: A recording for which the AudioChoice backend holds Scan_Events, produced
  by scanning an audio file that a listener imported.
- **Scan_Event**: A flagged span of a Scanned_Edition, carrying a start time, an end time, a
  category and a group, as already defined by `ScanEvent` in the shared contracts.
- **Filter_Window**: A start and end time pair, expressed in Book_Time, derived from the
  Scan_Events the listener has left enabled.
- **Book_Time**: Elapsed time from the first audio sample of the complete recording, measured in
  seconds, independent of playback speed and independent of how the recording is divided into
  parts.
- **Session_Time**: The position value a Libby_Session reports, which may be measured from the
  start of the current part rather than from the start of the recording.
- **Part_Map**: The ordered list of part durations for a Scanned_Edition, indexed from the first
  part, that converts Session_Time to Book_Time. Where a Libby_Session reports a part index, that
  index selects the entry of the Part_Map from which the conversion proceeds.
- **Alignment_Offset**: A signed number of seconds added to a converted Book_Time to correct for
  audio present in one rendition of a recording and absent from the other, such as a publisher
  announcement.
- **Book_Position**: The listener's current position in the recording, expressed in Book_Time.
- **Filter_Enforcer**: The AudioChoice component that prevents a Filter_Window from being heard.
- **Mute_Enforcement**: Filter_Enforcer behaviour that silences the device media output stream for
  the duration of a Filter_Window.
- **Skip_Enforcement**: Filter_Enforcer behaviour that commands the Libby_Session to seek to the
  end of a Filter_Window.
- **Enforcement_Selection**: The listener's choice, stored per Scanned_Edition, between
  Mute_Enforcement and Skip_Enforcement.
- **Stored_Restore_Volume**: The device media output stream volume value recorded immediately
  before the Filter_Enforcer reduces that volume to zero, and the value the device media output
  stream volume is set back to afterwards.
- **Coverage_Advisor**: The AudioChoice component that reports whether Companion_Filtering is
  available for the audio in a Libby_Session.
- **Coverage_Status**: One of the mutually exclusive statuses the Coverage_Advisor reports for a
  Libby_Session: filtering active, awaiting listener confirmation, position not determinable, no
  scan available, coverage could not be determined, or coverage check in progress.
- **Notification_Access**: The Android setting that binds AudioChoice's
  `NotificationListenerService` and thereby permits reading and controlling External_Sessions.
- **Legal_Review_Record**: The recorded outcome of legal review of Companion_Filtering, following
  the precedent of the ownership acknowledgment recorded by `LocalAaxConverter`.
- **Loaned_Audio**: The audio content of an OverDrive loan, in any encoded, encrypted or decoded
  form.

## Requirements

### Requirement 1: Licensing and Content Boundary

**User Story:** As the owner of AudioChoice, I want Companion_Filtering to stay clear of digital
rights circumvention, so that the feature can ship without exposing the product to a
circumvention claim.

#### Acceptance Criteria

1. THE Companion_Filter_Service SHALL restrict its interaction with a Libby_Session to reading the
   published metadata fields title, author, album and duration, reading the published playback
   state, playback position and playback speed, reading the session's declared transport actions,
   and issuing only play, pause and seek-to transport commands.
2. THE Companion_Filter_Service SHALL derive Book_Position solely from the position, playback state
   and playback speed published by the Libby_Session, the device clock, the stored Part_Map for the
   matched Scanned_Edition, and the stored Alignment_Offset for that Scanned_Edition.
3. THE Companion_Filter_Service SHALL derive Filter_Windows solely from Scan_Events held by the
   AudioChoice backend for a Scanned_Edition and from the listener's filter control choices, and
   SHALL derive no Filter_Window from Loaned_Audio.
4. IF a Companion_Filtering operation would require reading, decoding, decrypting, recording,
   copying, transcribing or storing Loaned_Audio, THEN THE Companion_Filter_Service SHALL not
   perform that operation, SHALL leave Companion_Filtering inactive for the affected Libby_Session,
   and SHALL report to the listener that filtering is unavailable for that session.
5. THE AudioChoice Android application SHALL declare no permission granting microphone input
   capture or media output capture, and SHALL invoke no audio capture interface, in any build in
   which Companion_Filtering is present.
6. WHEN a listener enables Companion_Filtering for the first time, THE Companion_Filter_Service
   SHALL present a statement that AudioChoice reads playback position and issues playback
   commands, and that AudioChoice does not access the audio itself, and SHALL observe no
   External_Session and keep Companion_Filtering inactive until the listener acknowledges that
   statement.
7. WHERE the Legal_Review_Record for Companion_Filtering is absent or is not marked complete, THE
   AudioChoice Android application SHALL omit the Companion_Filtering setting from production
   builds and SHALL prevent the Companion_Filter_Service from starting in those builds.
8. WHEN the Companion_Filter_Service persists or transmits data derived from a Libby_Session, THE
   Companion_Filter_Service SHALL limit that data to the identifier of the matched
   Scanned_Edition, Book_Time position values, the Alignment_Offset in effect and filter control
   choices, and SHALL include no Loaned_Audio in any encoded, encrypted or decoded form.
9. THE Companion_Filter_Service SHALL request, receive and store no OverDrive account credential
   and no library-card credential, and SHALL issue no request to an OverDrive service as part of
   Companion_Filtering.

### Requirement 2: Enabling Companion Filtering

**User Story:** As a listener who borrows audiobooks from my library, I want to turn on filtering
for Libby playback, so that I can use my library loans without hearing content I have chosen to
exclude.

#### Acceptance Criteria

1. THE AudioChoice Android application SHALL present a Companion_Filtering setting that a listener
   can enable and disable, and SHALL present alongside it whether Notification_Access is currently
   granted.
2. WHEN a listener enables Companion_Filtering and Notification_Access is not granted, THE
   AudioChoice Android application SHALL present an explanation that AudioChoice needs
   Notification_Access to observe Libby's playback controls, SHALL offer to open the Android
   Notification_Access settings screen, and SHALL keep Companion_Filtering recorded as disabled
   until Notification_Access is granted.
3. WHEN a listener enables Companion_Filtering and Notification_Access is granted, THE
   Companion_Filter_Service SHALL record Companion_Filtering as enabled and SHALL begin observing
   External_Sessions within 2.0 seconds.
4. WHEN a listener disables Companion_Filtering, THE Companion_Filter_Service SHALL stop observing
   External_Sessions within 2.0 seconds and SHALL release any device media volume adjustment it
   holds, restoring the Stored_Restore_Volume, within 1.0 second.
5. IF Notification_Access is revoked while Companion_Filtering is enabled, THEN THE
   Companion_Filter_Service SHALL release any device media volume adjustment it holds within 1.0
   second, restoring the Stored_Restore_Volume, SHALL record Companion_Filtering as disabled, and
   SHALL present a message indicating that filtering has stopped because Notification_Access is no
   longer granted.
6. WHILE Companion_Filtering is active for a Libby_Session, THE Companion_Filter_Service SHALL run
   as a foreground service and SHALL display, within 2.0 seconds of activation, a notification
   naming the matched Scanned_Edition and the count of enabled filter controls.
7. THE AudioChoice Android application SHALL keep Companion_Filtering disabled by default.
8. IF a listener returns from the Android Notification_Access settings screen without
   Notification_Access having been granted, THEN THE AudioChoice Android application SHALL leave
   Companion_Filtering recorded as disabled, SHALL leave External_Sessions unobserved, and SHALL
   present a message indicating that filtering cannot start without Notification_Access.
9. WHEN the AudioChoice Android application starts while Companion_Filtering is recorded as enabled
   and Notification_Access is granted, THE Companion_Filter_Service SHALL resume observing
   External_Sessions within 5.0 seconds without further listener action.

### Requirement 3: Detecting and Attaching to Libby Playback

**User Story:** As a listener, I want AudioChoice to notice when I start a Libby audiobook, so
that I do not have to set filtering up each time I press play.

#### Acceptance Criteria

1. WHILE Companion_Filtering is enabled, THE Session_Observer SHALL subscribe to changes in the
   set of active External_Sessions, and SHALL evaluate every External_Session that is already
   active at the moment of subscription within 1.0 second of subscribing.
2. WHEN a Libby_Session becomes active, meaning the Libby package appears in the set of active
   External_Sessions with a published playback state, THE Session_Observer SHALL read the
   session's title, author, album, total duration and playback state within 1.0 second.
3. WHILE a Libby_Session reports a playback state of playing, THE Session_Observer SHALL publish
   Book_Position to the Companion_Filter_Service at intervals of 100 milliseconds or shorter.
4. WHEN a Libby_Session becomes inactive, meaning it is removed from the set of active
   External_Sessions, THE Companion_Filter_Service SHALL release any device media volume
   adjustment it holds within 500 milliseconds, restoring the Stored_Restore_Volume.
5. WHEN a Libby_Session reports metadata identifying a recording different from the currently
   matched Scanned_Edition, THE Edition_Matcher SHALL discard the Filter_Windows, Part_Map and
   Alignment_Offset held for the previous Scanned_Edition, SHALL leave Companion_Filtering
   inactive for the new recording until matching completes, and SHALL repeat matching for the new
   recording.
6. THE Session_Observer SHALL restrict Companion_Filtering to External_Sessions whose owning
   package is a Supported_Player, and SHALL derive no Book_Position from and issue no transport
   command to any other External_Session.
7. WHEN a Libby_Session becomes active, and again whenever that session changes its declared
   transport actions, THE Session_Observer SHALL record whether the session declares support for a
   seek-to transport action.
8. IF a Libby_Session becomes active while reporting an empty title, an empty author, or a total
   duration that is absent or not greater than 0.0 seconds, THEN THE Companion_Filter_Service
   SHALL leave Companion_Filtering inactive for that Libby_Session, SHALL issue no transport
   command to it, and SHALL report that AudioChoice cannot identify the recording.
9. WHEN a Libby_Session becomes inactive, THE Session_Observer SHALL stop publishing Book_Position
   within 500 milliseconds and SHALL treat any Filter_Window pending for that session as no longer
   pending.
10. IF more than one Libby_Session is active at the same time, THEN THE Session_Observer SHALL
    apply Companion_Filtering to the Libby_Session that most recently reported a playback state of
    playing and SHALL derive no Book_Position from the others.

### Requirement 4: Matching Libby Audio to a Scanned Edition

**User Story:** As a listener, I want AudioChoice to be certain which recording I am hearing, so
that filtering removes the passages it is supposed to remove and nothing else.

#### Acceptance Criteria

1. WHEN a Libby_Session becomes active and no stored confirmation applies to the recording it
   reports, THE Edition_Matcher SHALL assemble a candidate list of at most 20 Scanned_Editions from
   the title, author, narrator where the Libby_Session reports one, and total duration reported by
   the Libby_Session, because no file is available from which to compute a `BookFingerprint`.
2. IF a candidate's total duration differs from the duration reported by the Libby_Session by
   2.0 seconds or less and the candidate's normalized title and normalized author are each equal,
   character for character, to the corresponding normalized value reported by the Libby_Session,
   THEN THE Edition_Matcher SHALL treat that candidate as a strong match.
3. WHEN the Edition_Matcher finds exactly one strong match, THE Edition_Matcher SHALL present that
   Scanned_Edition to the listener for confirmation and SHALL leave Companion_Filtering inactive,
   enforcing no Filter_Window, until the listener confirms it.
4. WHEN the Edition_Matcher finds more than one strong match, THE Edition_Matcher SHALL present at
   most 5 candidates ordered by ascending duration difference, ordered by ascending normalized
   title where two candidates share the same duration difference, and SHALL leave
   Companion_Filtering inactive until the listener chooses one.
5. IF the Edition_Matcher finds no strong match, THEN THE Coverage_Advisor SHALL report, within
   3.0 seconds of the Libby_Session becoming active, that AudioChoice holds no scan for the
   recording and SHALL leave Companion_Filtering inactive for that Libby_Session.
6. WHEN a listener confirms a Scanned_Edition for a recording, THE Edition_Matcher SHALL store that
   confirmation together with the normalized title, normalized author and reported duration, and
   SHALL reuse it without prompting again for any later Libby_Session whose normalized title and
   normalized author equal the stored values and whose reported duration differs from the stored
   duration by 2.0 seconds or less.
7. THE Edition_Matcher SHALL allow a listener to withdraw a stored confirmation for a
   Scanned_Edition.
8. THE Edition_Matcher SHALL normalize a title for comparison by applying the rules already applied
   by `EditionTitleCleaner` and then folding case and collapsing each run of whitespace to a single
   space, so that a title matches consistently across the import path and the Companion_Filtering
   path.
9. THE Edition_Matcher SHALL normalize an author for comparison by folding case, removing
   punctuation, and collapsing each run of whitespace to a single space.
10. WHEN a listener withdraws a stored confirmation, THE Edition_Matcher SHALL discard that
    confirmation, SHALL leave Companion_Filtering inactive for any Libby_Session currently matched
    through it, and SHALL repeat matching for the recording reported by that Libby_Session.
11. IF the Libby_Session reports no title, or no author, or a total duration of zero seconds or
    less, THEN THE Edition_Matcher SHALL treat the recording as having no strong match.

### Requirement 5: Converting Session Position to Book Position

**User Story:** As a listener, I want a filter to fire at the right moment, so that the passage is
removed rather than the words on either side of it.

#### Acceptance Criteria

1. THE Session_Observer SHALL compute Book_Position as the Session_Time converted through the
   Part_Map, plus the Alignment_Offset, expressed in seconds to a resolution of 0.01 seconds and
   clamped to the range 0.0 seconds to the total duration of the matched Scanned_Edition.
2. WHERE a Libby_Session reports a duration that differs from the total duration of the matched
   Scanned_Edition by 2.0 seconds or less, THE Session_Observer SHALL treat Session_Time as
   measured from the start of the complete recording and SHALL use an identity Part_Map.
3. WHERE a Libby_Session reports a duration that differs from the total duration of the matched
   Scanned_Edition by more than 2.0 seconds and reports a part index within the Part_Map, THE
   Session_Observer SHALL treat Session_Time as measured from the start of that part and SHALL add
   the summed durations of all parts preceding that index.
4. WHEN a Libby_Session publishes a position, THE Session_Observer SHALL extrapolate Book_Position
   at intervals of 100 milliseconds or shorter using the elapsed device clock time and the reported
   playback speed, using a playback speed of 1.0 WHERE the Libby_Session reports no playback speed,
   and treating any reported playback speed outside the range 0.25 to 4.0 as 1.0.
5. THE Session_Observer SHALL keep extrapolated Book_Position within 250 milliseconds of the value
   computed from the position the Libby_Session publishes at its next position update, while the
   reported playback speed is unchanged and no seek has occurred between the two publications.
6. WHEN a Libby_Session reports a change of playback speed, THE Session_Observer SHALL apply the
   new speed to subsequent extrapolation, within 100 milliseconds of the reported change.
7. WHEN a Libby_Session reports a playback state other than playing, THE Session_Observer SHALL
   hold Book_Position at its last computed value and SHALL stop extrapolating from the device clock.
8. IF the Part_Map for a matched Scanned_Edition cannot be established, meaning that no part
   durations are available for that Scanned_Edition or that the summed part durations differ from
   the total duration of that Scanned_Edition by more than 2.0 seconds, THEN THE Coverage_Advisor
   SHALL report that AudioChoice cannot locate the listener's position in the recording and SHALL
   leave Companion_Filtering inactive for that Libby_Session.
9. WHEN a Libby_Session publishes a position, THE Session_Observer SHALL replace any extrapolated
   Book_Position with the value computed from the published Session_Time and SHALL re-anchor
   subsequent extrapolation to that published position and the device clock time at which it was
   read, whatever the reported playback state.
10. IF a Libby_Session reports a playback state of playing and publishes no position update for
    30.0 seconds, THEN THE Session_Observer SHALL report Book_Position as unverified until the next
    published position is read.

### Requirement 6: Aligning the Two Renditions of a Recording

**User Story:** As a listener, I want to correct a filter that fires slightly early or slightly
late, so that a leading announcement in the library rendition does not throw every filter off.

#### Acceptance Criteria

1. THE Companion_Filter_Service SHALL maintain one Alignment_Offset per matched Scanned_Edition,
   and SHALL use 0.0 seconds as the Alignment_Offset for a matched Scanned_Edition that has no
   stored Alignment_Offset.
2. WHILE Companion_Filtering is active for a Libby_Session, THE Companion_Filter_Service SHALL
   present the Alignment_Offset in effect and SHALL accept a listener-supplied Alignment_Offset
   from -120.0 seconds to 120.0 seconds inclusive, in increments of 0.5 seconds.
3. WHEN a listener changes the Alignment_Offset, THE Companion_Filter_Service SHALL apply the new
   value to Book_Position within 500 milliseconds, SHALL use the new value for every Filter_Window
   whose start has not yet been reached, and SHALL complete any Filter_Window already being
   enforced under the value that was in effect when that Filter_Window began.
4. WHEN a listener sets an Alignment_Offset and later sets its negation, THE
   Companion_Filter_Service SHALL compute a Book_Position for a given Session_Time within
   1 millisecond of the value it computed for that same Session_Time before either change.
5. WHERE the duration reported by the Libby_Session differs from the duration of the matched
   Scanned_Edition by more than 2.0 seconds, THE Companion_Filter_Service SHALL propose, for the
   listener to accept or decline, an Alignment_Offset equal to the duration reported by the
   Libby_Session minus the duration of the matched Scanned_Edition, rounded to the nearest 0.5
   seconds and limited to the range -120.0 seconds to 120.0 seconds.
6. THE Companion_Filter_Service SHALL store the Alignment_Offset for a Scanned_Edition, SHALL
   retain it across restarts of the AudioChoice Android application, and SHALL reuse the stored
   value, in preference to any proposed value, for later Libby_Sessions reporting the same
   recording.
7. IF a listener supplies an Alignment_Offset outside the range -120.0 seconds to 120.0 seconds or
   not an increment of 0.5 seconds, THEN THE Companion_Filter_Service SHALL reject the supplied
   value, SHALL retain the Alignment_Offset previously in effect, and SHALL report a message
   indicating the accepted range and increment.
8. WHILE a proposed Alignment_Offset is awaiting a listener response, THE Companion_Filter_Service
   SHALL leave the Alignment_Offset in effect unchanged.
9. IF adding the Alignment_Offset to a converted Book_Time yields a value below 0.0 seconds, THEN
   THE Session_Observer SHALL report Book_Position as 0.0 seconds, and IF it yields a value above
   the duration of the matched Scanned_Edition, THEN THE Session_Observer SHALL report
   Book_Position as that duration.

### Requirement 7: Silencing a Flagged Span

**User Story:** As a listener, I want flagged content to be inaudible, so that I get the benefit of
filtering even when Libby will not let AudioChoice move the playhead.

#### Acceptance Criteria

1. THE Filter_Enforcer SHALL derive Filter_Windows, expressed in Book_Time, from the Scan_Events of
   the matched Scanned_Edition, excluding Scan_Events whose category, group, event key or aggregate
   key the listener has disabled, and SHALL combine enabled Scan_Events that overlap or adjoin into
   a single Filter_Window using the same connected-block rule that `FilterSkipPlanner` applies to
   AudioChoice's own player.
2. WHEN Book_Position reaches a point 150 milliseconds before the start of an enabled
   Filter_Window, THE Filter_Enforcer SHALL record the current device media output stream volume as
   the Stored_Restore_Volume and SHALL set the device media output stream volume to zero within
   50 milliseconds of that point.
3. WHEN Book_Position reaches the end of an enabled Filter_Window, THE Filter_Enforcer SHALL set
   the device media output stream volume to the Stored_Restore_Volume within 150 milliseconds of
   that point and SHALL then hold no volume adjustment for that Filter_Window.
4. IF the Filter_Enforcer cannot set the device media output stream volume to zero for an enabled
   Filter_Window, THEN THE Filter_Enforcer SHALL report that the Filter_Window was not enforced and
   SHALL leave the Stored_Restore_Volume unchanged.
5. WHILE the device media output stream volume is set to zero for a Filter_Window, THE
   Filter_Enforcer SHALL treat a further request to silence for an overlapping Filter_Window as
   already satisfied, SHALL leave the Stored_Restore_Volume unchanged, and SHALL extend the restore
   point to the later of the two Filter_Window ends.
6. IF the device media output stream volume is observed to differ from zero while a Filter_Window is
   being enforced, THEN THE Filter_Enforcer SHALL record the observed value as the
   Stored_Restore_Volume and SHALL set the device media output stream volume to zero again within
   150 milliseconds of the observation.
7. IF the Companion_Filter_Service stops for any reason while the device media output stream volume
   is set to zero, THEN THE Companion_Filter_Service SHALL set the device media output stream
   volume to the Stored_Restore_Volume before stopping.
8. WHEN a Libby_Session reports a playback state other than playing while a Filter_Window is being
   enforced, THE Filter_Enforcer SHALL set the device media output stream volume to the
   Stored_Restore_Volume within 150 milliseconds of that report and SHALL enforce the span from the
   Book_Position at which playback resumes to the end of that Filter_Window when playback resumes.
9. WHEN Book_Position changes by more than 1.0 second beyond the change expected from elapsed
   device clock time and reported playback speed, and the new Book_Position falls inside an enabled
   Filter_Window, THE Filter_Enforcer SHALL record the current device media output stream volume as
   the Stored_Restore_Volume and SHALL set the device media output stream volume to zero within
   150 milliseconds.
10. WHEN Book_Position changes by more than 1.0 second beyond the change expected from elapsed
    device clock time and reported playback speed, and the new Book_Position falls outside every
    enabled Filter_Window while the device media output stream volume is set to zero, THE
    Filter_Enforcer SHALL set the device media output stream volume to the Stored_Restore_Volume
    within 150 milliseconds.
11. WHEN the Companion_Filter_Service starts and a Stored_Restore_Volume from a previous run is
    recorded as not yet restored, THE Companion_Filter_Service SHALL set the device media output
    stream volume to that value and SHALL clear that record.

### Requirement 8: Skipping a Flagged Span

**User Story:** As a listener, I want flagged content skipped rather than silenced where that
works, so that I do not sit through silence in place of the passage.

#### Acceptance Criteria

1. WHERE a Libby_Session has been recorded as declaring support for a seek-to transport action and
   the Enforcement_Selection for the matched Scanned_Edition is Skip_Enforcement, THE
   Filter_Enforcer SHALL use Skip_Enforcement in place of Mute_Enforcement for every enabled
   Filter_Window scheduled after that selection.
2. WHEN Book_Position reaches a point 250 milliseconds before the start of an enabled
   Filter_Window under Skip_Enforcement, THE Filter_Enforcer SHALL command the Libby_Session to
   seek to the Session_Time corresponding to the end of the connected block of enabled
   Filter_Windows, computed with the same connected-block rule and the same 0.25 second look-ahead
   that `FilterSkipPlanner` applies to AudioChoice's own player.
3. WHEN the Filter_Enforcer issues a seek command under Skip_Enforcement, THE Filter_Enforcer SHALL
   apply Mute_Enforcement from before that command is issued until the Libby_Session publishes a
   Book_Position later than the end of that connected block of enabled Filter_Windows, so that the
   flagged span stays inaudible while the seek completes.
4. IF the Libby_Session does not publish a Book_Position later than the end of the connected block
   of enabled Filter_Windows within 2.0 seconds of a seek command, THEN THE Filter_Enforcer SHALL
   treat that seek as unsuccessful, SHALL apply Mute_Enforcement for the remainder of that
   connected block, and SHALL record seeking as unreliable for that Libby_Session.
5. IF the Filter_Enforcer has recorded seeking as unreliable for a Libby_Session, THEN THE
   Filter_Enforcer SHALL use Mute_Enforcement for every remaining enabled Filter_Window in that
   Libby_Session and SHALL retain the listener's Enforcement_Selection for later Libby_Sessions.
6. WHERE a Libby_Session has not been recorded as declaring support for a seek-to transport action,
   THE Companion_Filter_Service SHALL present Skip_Enforcement as unavailable and not selectable,
   and SHALL use Mute_Enforcement for every enabled Filter_Window in that Libby_Session.
7. THE Companion_Filter_Service SHALL use Mute_Enforcement for a matched Scanned_Edition that has
   no stored Enforcement_Selection.
8. IF the first Book_Position a Libby_Session publishes after a seek command under
   Skip_Enforcement falls inside an enabled Filter_Window, THEN THE Filter_Enforcer SHALL issue one
   further seek command for that connected block and SHALL treat a second landing inside an enabled
   Filter_Window as an unsuccessful seek.
9. WHERE Skip_Enforcement is in use, WHEN Book_Position moves backward into an enabled
   Filter_Window that has already been enforced in the Libby_Session, THE Filter_Enforcer SHALL
   enforce that Filter_Window again on the same terms as its first enforcement.
10. IF the end of the connected block of enabled Filter_Windows falls at or beyond a point
    0.25 seconds before the total duration reported by the Libby_Session, THEN THE Filter_Enforcer
    SHALL apply Mute_Enforcement for that connected block instead of issuing a seek command.

### Requirement 9: Choosing What Gets Filtered

**User Story:** As a listener, I want the same filter controls for a library loan as for a book I
own, so that my choices mean the same thing whatever the source.

#### Acceptance Criteria

1. WHEN a listener confirms a Scanned_Edition for a Libby_Session, THE Companion_Filter_Service
   SHALL present, within 3.0 seconds, one filter control for each category, group, event key and
   aggregate key that `PlaybackFilterTaxonomy` derives from the Scan_Events of that
   Scanned_Edition, and SHALL present no control for a category or group the Scanned_Edition holds
   no Scan_Event for.
2. WHEN a listener enables or disables a filter control for a matched Scanned_Edition, THE
   Companion_Filter_Service SHALL recompute Filter_Windows within 500 milliseconds of the change,
   SHALL apply the choice to every Scan_Event that control governs, including every Scan_Event
   sharing an aggregate key with that control, and SHALL apply the recomputed Filter_Windows to
   every Filter_Window whose start is later than the Book_Position at the time of the change.
3. WHEN a listener enables or disables a filter control for a matched Scanned_Edition, THE
   Companion_Filter_Service SHALL persist that choice through the same `BookFilterSettings` record
   used by AudioChoice's own player for that Scanned_Edition, so that a choice made during
   Companion_Filtering and a choice made in AudioChoice's own player produce the same set of
   Filter_Windows for that Scanned_Edition.
4. WHERE parental controls restrict changes to filter settings, THE Companion_Filter_Service SHALL
   present the stored filter control choices in a state that accepts no change, SHALL leave the
   stored choices unchanged, and SHALL indicate that the choices are restricted by parental
   controls.
5. WHEN a listener enables Companion_Filtering for a matched Scanned_Edition and AudioChoice holds
   no `BookFilterSettings` record for that Scanned_Edition, THE Companion_Filter_Service SHALL
   enable every filter control presented for that Scanned_Edition and SHALL exclude no Scan_Event
   from Filter_Windows on the basis of a filter control choice.
6. IF a listener disables a filter control while a Filter_Window derived solely from Scan_Events
   that control governs is being enforced, THEN THE Filter_Enforcer SHALL end enforcement of that
   Filter_Window and SHALL restore the device media output stream volume within 150 milliseconds of
   the change.
7. IF persisting a filter control choice to the `BookFilterSettings` record fails, THEN THE
   Companion_Filter_Service SHALL apply the choice to Filter_Windows for the remainder of the
   Libby_Session, SHALL retain the choice on the device, and SHALL report that the choice is not
   yet saved to the listener's account.
8. IF the matched Scanned_Edition holds a Scan_Event whose group `PlaybackFilterTaxonomy` defines
   no control for, THEN THE Companion_Filter_Service SHALL exclude that Scan_Event from
   Filter_Windows, so that no span is enforced without a control the listener can disable.

### Requirement 10: Reporting Coverage Honestly

**User Story:** As a listener, I want to know before I start listening whether AudioChoice can
filter a loan, so that I am not surprised by unfiltered content part-way through a book.

#### Acceptance Criteria

1. WHEN a Libby_Session becomes active, THE Coverage_Advisor SHALL report exactly one
   Coverage_Status from the following: filtering active naming the matched Scanned_Edition,
   awaiting listener confirmation naming each candidate Scanned_Edition, position not determinable
   for the matched Scanned_Edition, or no scan available.
2. WHILE Companion_Filtering is active for a Libby_Session, THE Coverage_Advisor SHALL report the
   count of enabled Filter_Windows for the matched Scanned_Edition and the count of Filter_Windows
   already enforced in that Libby_Session.
3. WHEN the Coverage_Advisor reports that no scan is available for a recording, THE
   Coverage_Advisor SHALL name the title and author reported by the Libby_Session and SHALL state
   that AudioChoice can filter the recording once the edition has been scanned from an imported
   copy.
4. WHEN a Libby_Session becomes active, THE Coverage_Advisor SHALL report a Coverage_Status for
   that Libby_Session within 3.0 seconds.
5. WHILE no Libby_Session is active, THE AudioChoice Android application SHALL allow a listener to
   check coverage for a recording by entering a title of 1 to 200 characters and an author of up to
   200 characters, and SHALL report within 5.0 seconds either at most 20 matching Scanned_Editions
   ordered by descending closeness of normalized title match, or that no scan is available.
6. IF the AudioChoice backend does not respond to a coverage request within 5.0 seconds, THEN THE
   Coverage_Advisor SHALL determine coverage from the locally stored Scan_Events of previously
   matched Scanned_Editions and SHALL report that coverage was determined from stored data.
7. WHEN a Filter_Window is enforced during a Libby_Session for which Companion_Filtering is active,
   THE Coverage_Advisor SHALL update the reported count of enforced Filter_Windows within
   1.0 second.
8. IF the Coverage_Advisor has not determined a final Coverage_Status within 3.0 seconds of a
   Libby_Session becoming active, THEN THE Coverage_Advisor SHALL report a
   coverage-check-in-progress status within 3.0 seconds and SHALL replace it with a final
   Coverage_Status within 15.0 seconds of that session becoming active.
9. IF the AudioChoice backend does not respond to a coverage request within 5.0 seconds and no
   locally stored Scan_Events exist for the recording, THEN THE Coverage_Advisor SHALL report that
   coverage could not be determined and SHALL leave Companion_Filtering inactive for that
   Libby_Session.

### Requirement 11: Reporting a Filter That Got It Wrong

**User Story:** As a listener, I want to report a filter mistake during a library loan, so that the
scan improves for everyone using that edition.

#### Acceptance Criteria

1. WHILE Companion_Filtering is active for a Libby_Session, THE Companion_Filter_Service SHALL
   present a control by which a listener reports that flagged content was heard, and SHALL take the
   Book_Position held at the moment the control is used as the reported position, covering the
   look-back window that `FilterReportComposer` applies.
2. WHILE Companion_Filtering is active for a Libby_Session, THE Companion_Filter_Service SHALL
   present a control by which a listener reports that a Filter_Window removed content that should
   have played, identifying the most recently enforced Filter_Window in that Libby_Session and,
   WHERE that Filter_Window derives from a single Scan_Event, that Scan_Event.
3. WHEN a listener submits a report during Companion_Filtering, THE Companion_Filter_Service SHALL
   compose the report through `FilterReportComposer` using the stored `BookFingerprint` of the
   matched Scanned_Edition and positions expressed in Book_Time, so that a report carries a
   position and carries no Loaned_Audio and no transcription of what was heard.
4. WHEN a listener submits a report during Companion_Filtering, THE Companion_Filter_Service SHALL
   record the Alignment_Offset in effect at the moment of submission, as a value between
   -120.0 seconds and 120.0 seconds, because a systematic timing error is indistinguishable from a
   scan error without it.
5. IF a listener submits a report while the AudioChoice backend cannot be reached, THEN THE
   Companion_Filter_Service SHALL store the composed report on the device, retain it across
   restarts of the Companion_Filter_Service, and submit it when the backend becomes reachable.
6. WHEN the Companion_Filter_Service stores or submits a report, THE Companion_Filter_Service SHALL
   confirm to the listener within 1.0 second that the report was recorded, stating the reported
   Book_Position.
7. IF no Filter_Window has been enforced in the Libby_Session, THEN THE Companion_Filter_Service
   SHALL present the control for reporting a wrongly removed Filter_Window as unavailable.

### Requirement 12: Distribution Constraints

**User Story:** As the owner of AudioChoice, I want the platform requirements for Companion
Filtering understood before implementation, so that the feature is not blocked at store review
after it is built.

#### Acceptance Criteria

1. THE AudioChoice Android application SHALL declare Notification_Access with Companion_Filtering
   named as the only feature that uses it, and SHALL name no other feature as a user of
   Notification_Access, because Google Play treats Notification_Access as a sensitive capability.
2. THE Companion_Filter_Service SHALL declare exactly one foreground service type, and that type
   SHALL NOT be the media playback type, because AudioChoice publishes no media session and plays
   no audio of its own during Companion_Filtering.
3. WHILE Notification_Access is not granted, THE AudioChoice Android application SHALL keep every
   imported-audiobook capability available and unchanged, covering import, scan, playback,
   filtering and filter reporting, with no capability withheld, blocked or degraded.
4. THE AudioChoice iOS client SHALL present no Companion_Filtering setting, no Alignment_Offset
   control and no Coverage_Status report for a Libby_Session, because iOS provides no equivalent
   cross-application media session control.
5. IF the platform distributor declines or withdraws approval of the declared Notification_Access
   use, THEN THE AudioChoice Android application SHALL make Companion_Filtering unavailable, SHALL
   report to the listener that filtering of Libby playback is unavailable, and SHALL leave
   imported-audiobook filtering and stored filter control choices unchanged.
6. THE AudioChoice Android application SHALL restrict use of the External_Session metadata and
   playback state obtained through Notification_Access to Companion_Filtering, and SHALL transmit
   no such data off the device except within a listener-submitted report as defined in
   Requirement 11.
7. WHEN no Libby_Session has been active for 5.0 seconds and no Filter_Window is being enforced,
   THE Companion_Filter_Service SHALL stop running as a foreground service and SHALL remove its
   notification.

### Requirement 13: Library Availability and Handoff

**User Story:** As a listener, I want to see whether a book AudioChoice can filter is available at
my library, so that I can borrow the one that will work.

#### Acceptance Criteria

1. WHERE the normalized title and normalized author of a Scanned_Edition correspond to exactly one
   OverDrive catalog title, using the normalization rules already applied by `EditionTitleCleaner`,
   THE AudioChoice Android application SHALL present on that edition's detail view a link that
   opens that title in Libby.
2. WHEN a listener selects the link that opens a title in Libby, THE AudioChoice Android
   application SHALL present, before the handoff to Libby proceeds, a statement that borrowing and
   listening happen in Libby and that AudioChoice filters the playback through Companion_Filtering.
3. WHERE AudioChoice holds approved OverDrive Discovery API access, WHILE a listener has a selected
   library, WHEN a listener opens the detail view for a Scanned_Edition with a link that opens the
   title in Libby, THE AudioChoice Android application SHALL present the number of available
   copies, the hold queue length, and the time at which those values were retrieved, within
   5.0 seconds of the detail view opening and using values retrieved no more than 60 seconds
   earlier.
4. WHERE AudioChoice presents data supplied by an OverDrive API, THE AudioChoice Android
   application SHALL present the supplied values and wording as supplied, without alteration,
   truncation, rounding or recalculation, and SHALL include a link to the corresponding
   OverDrive-hosted page, as the OverDrive API usage requirements direct.
5. IF AudioChoice does not hold approved OverDrive API access, THEN THE AudioChoice Android
   application SHALL omit availability and hold information rather than deriving it from an
   unapproved source, and SHALL continue to present the link that opens the title in Libby.
6. IF no application on the device is registered to handle the link that opens a title in Libby,
   THEN THE AudioChoice Android application SHALL present an indication that Libby is required to
   borrow and listen, SHALL offer to install Libby, and SHALL leave the listener on the edition's
   detail view.
7. IF a request for availability and hold information fails or does not return within 5.0 seconds,
   THEN THE AudioChoice Android application SHALL present an indication that availability could not
   be retrieved, SHALL retain the link that opens the title in Libby, and SHALL offer the listener
   a retry, with no more than 3 automatic retries per detail view opening.
8. IF a listener has no selected library WHILE AudioChoice holds approved OverDrive Discovery API
   access, THEN THE AudioChoice Android application SHALL omit availability and hold information
   and SHALL present an indication that a library must be selected, together with a means of
   selecting one.
