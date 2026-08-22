alter table audiobook_editions
    add column if not exists explore_published boolean not null default true;
