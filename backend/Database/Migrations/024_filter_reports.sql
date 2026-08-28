-- Listener reports that filtering was wrong at a particular moment.
--
-- A real table rather than a JSON file, unlike the edition alias and signature caches:
-- those hold facts the resolver can rediscover, whereas a report is a one-off observation
-- from someone who heard the mistake. Losing one loses the only record that it happened.
--
-- Deliberately stores no audio and no transcript text. The position identifies the
-- passage, and the server already has the transcript for that edition.
create table if not exists filter_reports (
    id uuid primary key,
    user_id uuid not null references users(id) on delete cascade,
    -- The edition is identified by fingerprint rather than by a foreign key, so a report
    -- is still accepted when the edition row is missing. That case is exactly the one
    -- worth hearing about: no scan matched, so nothing was filtered.
    fingerprint_version integer not null,
    sha256 char(64) not null,
    file_size bigint not null,
    -- Text rather than an enum type so a later kind of report does not need a migration
    -- before the API can accept it.
    kind varchar(32) not null,
    position_seconds double precision not null,
    window_seconds double precision not null,
    scanner_version varchar(64),
    scan_event_id uuid,
    category_id uuid,
    reported_at timestamptz not null
);

-- Triage reads this newest-first, and grouping by edition is how a systematically bad
-- scan is told apart from a one-off mishearing.
create index if not exists filter_reports_reported_at_idx
    on filter_reports (reported_at desc);
create index if not exists filter_reports_edition_idx
    on filter_reports (fingerprint_version, sha256, file_size);
