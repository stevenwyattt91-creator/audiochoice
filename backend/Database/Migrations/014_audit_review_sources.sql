-- Review sources are supplied by an administrator for a single audit assignment.
-- Auditors receive only generated context clips, never a source-file download.
alter table audit_assignments
    add column if not exists review_media_status varchar(30) not null default 'waiting_for_source'
        check (review_media_status in ('waiting_for_source', 'ready', 'cleanup_pending', 'deleted'));

create table if not exists audit_review_sources (
    assignment_id uuid primary key references audit_assignments(id) on delete cascade,
    object_name varchar(600) not null,
    original_file_name varchar(300) not null,
    content_type varchar(120) not null,
    file_size bigint not null check (file_size > 0),
    uploaded_by uuid not null references users(id),
    uploaded_at timestamptz not null default now(),
    delete_after timestamptz,
    deleted_at timestamptz
);

create table if not exists audit_review_clips (
    assignment_id uuid not null references audit_assignments(id) on delete cascade,
    scan_event_id uuid not null references scan_events(id) on delete cascade,
    object_name varchar(600) not null,
    context_start_seconds double precision not null,
    context_end_seconds double precision not null,
    created_at timestamptz not null default now(),
    delete_after timestamptz,
    primary key (assignment_id, scan_event_id),
    check (context_start_seconds >= 0 and context_end_seconds > context_start_seconds)
);

create index if not exists audit_assignments_media_ready
    on audit_assignments(review_media_status, status, created_at desc);
