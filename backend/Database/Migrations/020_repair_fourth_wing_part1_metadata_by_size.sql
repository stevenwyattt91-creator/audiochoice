-- Reapply the canonical identity for Fourth Wing Part 1 after migration 019.
-- Some converted files have the same stable size but lose part metadata.
update audiobook_editions
set work_title = 'Fourth Wing (Part 1 of 2) (Dramatized Adaptation)',
    edition_type = 'Dramatized Adaptation',
    part_number = 1,
    total_parts = 2
where fingerprint_version = 1
  and (lower(sha256) = '3d37a3c485debd42249bc939deed657505d18c939bd43c00dae99e10800916e'
       or (file_size = 449954471 and lower(work_title) in ('fourth wing', 'fourth wing (dramatized adaptation)')));

update user_library_books lb
set title = 'Fourth Wing (Part 1 of 2) (Dramatized Adaptation)',
    author = coalesce(e.author, lb.author)
from audiobook_editions e
where lb.edition_id = e.id
  and e.fingerprint_version = 1
  and (lower(e.sha256) = '3d37a3c485debd42249bc939deed657505d18c939bd43c00dae99e10800916e'
       or (e.file_size = 449954471 and lower(e.work_title) in ('fourth wing', 'fourth wing (dramatized adaptation)')));
