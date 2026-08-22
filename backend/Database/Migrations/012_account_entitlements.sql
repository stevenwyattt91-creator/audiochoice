create table account_entitlements (
    id uuid primary key,
    user_id uuid not null references users(id) on delete cascade,
    plan varchar(80) not null,
    source varchar(40) not null,
    external_reference varchar(200),
    expires_at timestamptz,
    granted_at timestamptz not null default now(),
    revoked_at timestamptz,
    check (expires_at is null or expires_at > granted_at)
);
create index account_entitlements_access
    on account_entitlements(user_id, revoked_at, expires_at desc, granted_at desc);
create unique index account_entitlements_source_reference
    on account_entitlements(source, external_reference)
    where external_reference is not null and revoked_at is null;
