create table book_notes (
    id uuid primary key,
    user_id uuid not null references users(id) on delete cascade,
    library_book_id uuid not null references user_library_books(id) on delete cascade,
    position_seconds double precision,
    note_text varchar(10000) not null,
    created_at timestamptz not null,
    updated_at timestamptz not null,
    check (position_seconds is null or position_seconds >= 0)
);
create index book_notes_user_book on book_notes(user_id, library_book_id, updated_at desc);

create table library_collections (
    id uuid primary key,
    user_id uuid not null references users(id) on delete cascade,
    name varchar(100) not null,
    created_at timestamptz not null,
    updated_at timestamptz not null
);
create index library_collections_user on library_collections(user_id, updated_at desc);

create table library_collection_books (
    collection_id uuid not null references library_collections(id) on delete cascade,
    library_book_id uuid not null references user_library_books(id) on delete cascade,
    added_at timestamptz not null,
    primary key (collection_id, library_book_id)
);
create index library_collection_books_book on library_collection_books(library_book_id);
