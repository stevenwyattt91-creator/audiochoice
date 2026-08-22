alter table audiobook_editions
    add column if not exists cover_image bytea,
    add column if not exists cover_image_content_type text;
