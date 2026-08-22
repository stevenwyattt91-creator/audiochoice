create table users (
    id uuid primary key,
    email varchar(254) not null,
    display_name varchar(80) not null,
    email_verified boolean not null default false,
    created_at timestamptz not null,
    updated_at timestamptz not null
);
create index users_email_lookup on users (lower(email));

create table user_identities (
    id uuid primary key,
    user_id uuid not null references users(id) on delete cascade,
    provider varchar(30) not null,
    provider_subject varchar(255) not null,
    password_salt text,
    password_hash text,
    created_at timestamptz not null,
    unique (provider, provider_subject),
    check (
        (provider = 'password' and password_salt is not null and password_hash is not null)
        or (provider <> 'password' and password_salt is null and password_hash is null)
    )
);

create table user_sessions (
    token_hash char(64) primary key,
    user_id uuid not null references users(id) on delete cascade,
    provider varchar(30) not null,
    expires_at timestamptz not null,
    created_at timestamptz not null
);
create index user_sessions_user_id on user_sessions(user_id);
create index user_sessions_expires_at on user_sessions(expires_at);

create table account_action_tokens (
    token_hash char(64) primary key,
    user_id uuid not null references users(id) on delete cascade,
    purpose varchar(30) not null,
    expires_at timestamptz not null,
    created_at timestamptz not null,
    check (purpose in ('verify_email', 'reset_password', 'link_identity'))
);
create index account_action_tokens_expiry on account_action_tokens(expires_at);

create table audiobook_editions (
    id uuid primary key,
    fingerprint_version integer not null,
    sha256 char(64) not null,
    file_size bigint not null check (file_size > 0),
    duration_seconds double precision,
    file_type varchar(20) not null,
    work_title varchar(300),
    author varchar(300),
    narrator varchar(300),
    series_title varchar(300),
    series_number integer,
    edition_type varchar(100),
    part_number integer,
    total_parts integer,
    created_at timestamptz not null,
    unique (fingerprint_version, sha256, file_size)
);

create table user_library_books (
    id uuid primary key,
    user_id uuid not null references users(id) on delete cascade,
    edition_id uuid not null references audiobook_editions(id),
    title varchar(300) not null,
    author varchar(300),
    narrator varchar(300),
    cover_image_url varchar(500),
    playback_position_seconds double precision not null default 0 check (playback_position_seconds >= 0),
    is_finished boolean not null default false,
    is_favorite boolean not null default false,
    added_at timestamptz not null,
    updated_at timestamptz not null,
    unique (user_id, edition_id)
);
create index user_library_books_updated on user_library_books(user_id, updated_at desc);

create table bookmarks (
    id uuid primary key,
    user_id uuid not null references users(id) on delete cascade,
    library_book_id uuid not null references user_library_books(id) on delete cascade,
    position_seconds double precision not null check (position_seconds >= 0),
    title varchar(500),
    note varchar(2000),
    created_at timestamptz not null,
    updated_at timestamptz not null
);
create index bookmarks_book_position on bookmarks(library_book_id, position_seconds);

create table scan_results (
    id uuid primary key,
    edition_id uuid not null references audiobook_editions(id),
    scanner_version varchar(100) not null,
    taxonomy_version varchar(100) not null,
    scanned_at timestamptz not null,
    unique (edition_id, scanner_version)
);

create table scan_events (
    id uuid primary key,
    scan_result_id uuid not null references scan_results(id) on delete cascade,
    start_seconds double precision not null,
    end_seconds double precision not null,
    category_id uuid not null,
    group_id uuid not null,
    event_id uuid not null,
    confidence double precision not null check (confidence >= 0 and confidence <= 1),
    check (start_seconds >= 0 and end_seconds >= start_seconds)
);
create index scan_events_timeline on scan_events(scan_result_id, start_seconds);

create table scan_uploads (
    id uuid primary key,
    edition_id uuid not null references audiobook_editions(id),
    owner_user_id uuid not null references users(id) on delete cascade,
    object_name varchar(500) not null unique,
    expected_size bigint not null,
    content_type varchar(100) not null,
    status varchar(30) not null,
    expires_at timestamptz not null,
    delete_after timestamptz not null,
    created_at timestamptz not null,
    check (status in ('authorized', 'uploaded', 'processing', 'deleted', 'failed'))
);
create index scan_uploads_cleanup on scan_uploads(status, delete_after);

create table scan_jobs (
    id uuid primary key,
    edition_id uuid not null references audiobook_editions(id),
    upload_id uuid not null references scan_uploads(id),
    status varchar(30) not null,
    attempt_count integer not null default 0,
    available_at timestamptz not null,
    lease_owner varchar(200),
    lease_expires_at timestamptz,
    last_error varchar(2000),
    created_at timestamptz not null,
    updated_at timestamptz not null,
    check (status in ('queued', 'processing', 'completed', 'failed'))
);
create unique index scan_jobs_one_active_per_edition
    on scan_jobs(edition_id) where status in ('queued', 'processing');
create index scan_jobs_dequeue on scan_jobs(status, available_at);

create table private_transcripts (
    id uuid primary key,
    edition_id uuid not null references audiobook_editions(id),
    transcript_version varchar(100) not null,
    object_name varchar(500) not null unique,
    created_at timestamptz not null,
    unique (edition_id, transcript_version)
);

create table filter_profiles (
    id uuid primary key,
    user_id uuid not null references users(id) on delete cascade,
    name varchar(100) not null,
    is_active boolean not null default false,
    settings_json jsonb not null default '{}'::jsonb,
    created_at timestamptz not null,
    updated_at timestamptz not null
);
create index filter_profiles_user on filter_profiles(user_id);
