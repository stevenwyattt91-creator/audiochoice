alter table scan_events add column stable_key char(64);
update scan_events set stable_key = lpad(replace(id::text, '-', ''), 64, '0');
alter table scan_events alter column stable_key set default repeat('0', 64);
alter table scan_events alter column stable_key set not null;
alter table scan_events add column safe_description varchar(100) not null default 'Content event detected';
alter table scan_events add column aggregate_key char(64);
alter table scan_events add column aggregate_display varchar(100);
create index scan_events_stable_key on scan_events(scan_result_id, stable_key);

create table book_filter_settings (
    user_id uuid not null references users(id) on delete cascade,
    library_book_id uuid not null references user_library_books(id) on delete cascade,
    disabled_category_ids uuid[] not null default '{}',
    disabled_group_ids uuid[] not null default '{}',
    disabled_event_keys text[] not null default '{}',
    disabled_aggregate_keys text[] not null default '{}',
    updated_at timestamptz not null,
    primary key (user_id, library_book_id)
);
