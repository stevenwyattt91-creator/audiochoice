-- Final targeted repair for the imported Fourth Wing Part 1 row. The source
-- lost its fingerprint metadata, so match only the exact plain title, author,
-- and known 8-hour duration; do not alter other catalog books.
update audiobook_editions
set work_title = 'Fourth Wing (Part 1 of 2) (Dramatized Adaptation)',
    edition_type = 'Dramatized Adaptation',
    part_number = 1,
    total_parts = 2
where lower(btrim(work_title)) = 'fourth wing'
  and lower(coalesce(author, '')) = 'rebecca yarros'
  and duration_seconds between 28790 and 28810;

update user_library_books lb
set title = e.work_title,
    author = coalesce(e.author, lb.author)
from audiobook_editions e
where lb.edition_id = e.id
  and lower(btrim(e.work_title)) = 'fourth wing'
  and lower(coalesce(e.author, '')) = 'rebecca yarros'
  and e.duration_seconds between 28790 and 28810;
