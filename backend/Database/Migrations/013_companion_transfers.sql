create table companion_transfers (
    id uuid primary key,
    owner_user_id uuid not null references users(id) on delete cascade,
    file_name varchar(500) not null,
    content_type varchar(160) not null,
    expected_size bigint not null check (expected_size > 0),
    sha256 char(64) not null,
    receiver_code_hash char(64) not null,
    status varchar(20) not null,
    expires_at timestamptz not null,
    created_at timestamptz not null default now()
);
create index companion_transfers_owner on companion_transfers(owner_user_id, expires_at desc);
create index companion_transfers_expiry on companion_transfers(expires_at) where status not in ('deleted', 'received');
