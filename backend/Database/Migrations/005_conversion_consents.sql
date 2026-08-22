create table conversion_consents (
    id uuid primary key,
    user_id uuid not null references users(id) on delete cascade,
    user_email varchar(254) not null,
    user_display_name varchar(80) not null,
    fingerprint_version integer not null,
    sha256 char(64) not null,
    file_size bigint not null check (file_size > 0),
    duration_seconds double precision,
    file_type varchar(20) not null,
    source_file_name varchar(500) not null,
    agreement_version varchar(50) not null,
    agreement_text varchar(5000) not null,
    accepted_at timestamptz not null,
    unique (user_id, fingerprint_version, sha256, file_size, agreement_version)
);
create index conversion_consents_user_time on conversion_consents(user_id, accepted_at desc);
create index conversion_consents_fingerprint on conversion_consents(sha256, file_size);
