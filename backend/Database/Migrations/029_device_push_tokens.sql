-- Device tokens for push notifications.
--
-- A scan takes minutes, and until now the only way to learn it had finished was to sit on the
-- import screen. Android polled with a WorkManager job that Doze defers, and iOS could not poll in
-- the background at all, so both effectively told the listener when they next opened the app.
-- The server knows the moment a scan completes; this is what lets it say so.
create table device_push_tokens (
    id uuid primary key default gen_random_uuid(),
    user_id uuid not null references users(id) on delete cascade,
    -- 'apns' or 'fcm'. Stored rather than inferred so one table serves both platforms and the
    -- sender picks the transport per row.
    platform text not null,
    -- The token itself is the natural key: a device that reinstalls gets a new one, and the same
    -- device must never accumulate duplicate rows and receive a notification twice.
    token text not null,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    constraint device_push_tokens_token_unique unique (token)
);

-- Every send starts by asking which devices belong to the users waiting on a scan.
create index device_push_tokens_user on device_push_tokens(user_id);
