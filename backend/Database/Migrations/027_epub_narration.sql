-- EPUB narration: text-derived filter results, per-chapter synthesis records, the
-- premium-voice acknowledgement, and the measurements the design refuses to guess at.
--
-- Additive and forward-only, like every migration here. Nothing existing changes shape,
-- so a beta or release client sees identical responses throughout.
--
-- A narrated book needs no change to `audiobook_editions`: `duration_seconds` is already
-- nullable and `file_type` was widened to varchar(255) in 023, so a book whose fingerprint
-- says `epub` with no duration already fits. Library, favourites and progress
-- synchronisation therefore apply to a narrated book without knowing it is one.
--
-- No table here has a column for the book's text. That is the point rather than an
-- oversight: text arrives in a scan request, is held for that request, and is never
-- written down. A column would make the promise impossible to keep.

-- Text-derived filter results.
--
-- Deliberately separate from `scan_events` rather than a flag on it. Those carry seconds;
-- these carry character offsets into the book's text, and one query reading the other
-- would produce a filter skip measured in tens of hours. Keeping them apart is also what
-- keeps a narrated book out of the Explore catalogue, which is built from `scan_results`:
-- a text scan cannot become a catalogue entry because it never writes to that table.
create table if not exists narration_text_scans (
    id uuid primary key,
    -- Identified by fingerprint rather than by a foreign key, matching `filter_reports`,
    -- so a scan is still recorded when no edition row exists yet.
    fingerprint_version integer not null,
    sha256 char(64) not null,
    file_size bigint not null,
    language varchar(35),
    scanner_version varchar(64) not null,
    -- The taxonomy version the events were drawn from. Recorded so that "the same
    -- categories an audio scan uses" is checkable rather than asserted.
    taxonomy_version varchar(16) not null,
    -- The length of the text that was scanned, which is what makes an out-of-range event
    -- offset detectable later without keeping the text.
    book_text_characters integer not null,
    scanned_at timestamptz not null,
    unique (fingerprint_version, sha256, file_size, scanner_version)
);

-- One flagged passage, in characters.
--
-- Named `start_character`/`end_character` rather than reusing the second-shaped names, so
-- nothing on this side of the wire can confuse the two coordinate spaces. The client
-- carries them in a `ScanEvent`'s time fields because that buys the whole existing filter
-- stack unchanged, but the database has no reason to inherit that compromise.
create table if not exists narration_text_scan_events (
    id uuid primary key,
    scan_id uuid not null references narration_text_scans(id) on delete cascade,
    start_character integer not null,
    end_character integer not null,
    category_id uuid not null,
    group_id uuid not null,
    event_id uuid not null,
    confidence double precision not null,
    stable_key varchar(128) not null,
    safe_description text not null,
    aggregate_key varchar(128),
    aggregate_display text,
    -- Enforced here as well as in the client, because an inverted or negative range is a
    -- coordinate-space disagreement and storing one would spread the confusion.
    check (start_character >= 0 and end_character > start_character)
);

-- Read in offset order for one scan, which is how ranges are merged before synthesis.
create index if not exists narration_text_scan_events_scan_idx
    on narration_text_scan_events (scan_id, start_character);

-- Which provider produced each chapter of each listener's narration.
--
-- Per chapter rather than per book, because one book legitimately holds audio from more
-- than one voice: a premium entitlement that lapses mid-book keeps the chapters already
-- rendered and finishes the rest on an on-device voice. Without this, that book's audio
-- would be inexplicable.
create table if not exists narration_chapter_renders (
    id uuid primary key,
    user_id uuid not null references users(id) on delete cascade,
    fingerprint_version integer not null,
    sha256 char(64) not null,
    file_size bigint not null,
    chapter_index integer not null check (chapter_index >= 0),
    voice_id varchar(128) not null,
    provider varchar(64) not null,
    model_version varchar(128) not null,
    duration_seconds double precision not null check (duration_seconds >= 0),
    object_path varchar(512) not null,
    created_at timestamptz not null,
    -- One row per listener, book, chapter and voice. Re-rendering the same chapter with
    -- the same voice replaces rather than accumulates.
    unique (user_id, sha256, chapter_index, voice_id)
);

create index if not exists narration_chapter_renders_book_idx
    on narration_chapter_renders (user_id, sha256, chapter_index);

-- The listener's acknowledgement that selecting the premium voice sends the book's text
-- off their device.
--
-- Stores the agreement text, not just its version, so what someone actually agreed to can
-- be produced later. A version alone is only useful while the wording is still around.
create table if not exists narration_voice_acknowledgements (
    id uuid primary key,
    user_id uuid not null references users(id) on delete cascade,
    agreement_version varchar(32) not null,
    agreement_text text not null,
    accepted_at timestamptz not null,
    -- Idempotent on the version, so the offline delivery path can re-send without
    -- creating a second record.
    unique (user_id, agreement_version)
);

-- The values the design requires to be measured rather than assumed.
--
-- A table rather than configuration because the requirement is that a measurement be
-- recorded together with what it was measured on: a synthesis rate from an unnamed
-- instance type, or a device rate with no device, cannot be acted on or re-checked later.
-- The render-ahead window is recorded alongside the measurement it was derived from for
-- the same reason.
create table if not exists narration_measurements (
    id uuid primary key,
    -- premium_synthesis_rate | cold_start_delay | local_neural_synthesis_rate
    -- | billing_coverage_verified
    kind varchar(64) not null,
    measured_value double precision not null,
    measured_at timestamptz not null,
    -- The SageMaker instance type, or the device model, the measurement was taken on.
    target varchar(128) not null,
    software_version varchar(128) not null,
    render_ahead_window integer check (render_ahead_window is null or render_ahead_window >= 1),
    notes text
);

create index if not exists narration_measurements_kind_idx
    on narration_measurements (kind, measured_at desc);

-- A narrated book's filter report carries a character offset in `position_seconds`.
--
-- Renaming the column would have been cleaner and would have broken the iOS client, the
-- Android release build and the admin filter-report views at once. A defaulted column
-- instead: every existing row and every existing client means seconds, and nothing about
-- the request or response shape changes.
alter table filter_reports
    add column if not exists position_unit varchar(20) not null default 'seconds';

-- Constrained rather than free text, because triage reading the wrong coordinate space is
-- the exact failure this column exists to prevent.
alter table filter_reports
    drop constraint if exists filter_reports_position_unit_check;

alter table filter_reports
    add constraint filter_reports_position_unit_check
    check (position_unit in ('seconds', 'characterOffset'));
