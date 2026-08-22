-- Keep the stored display title deterministic for existing editions.
-- Fingerprints and edition identity are intentionally unchanged.
update audiobook_editions
set work_title = trim(
    regexp_replace(
        regexp_replace(
            regexp_replace(work_title, '\\s*\\(\\s*part\\s*[0-9]+\\s+of\\s+[0-9]+\\s*\\)', '', 'gi'),
            '\\s*\\((dramatized adaptation|full cast|graphic audio)\\)', '', 'gi'),
        '\\s+', ' ', 'g'))
where work_title is not null and btrim(work_title) <> '';

update audiobook_editions
set work_title = btrim(work_title) || ' (Part ' || part_number || ' of ' || total_parts || ')'
where part_number is not null and total_parts is not null and part_number > 0 and total_parts > 0
  and work_title not ilike '%(Part % of %)%';

update audiobook_editions
set work_title = btrim(work_title) || ' (Dramatized Adaptation)'
where edition_type is not null
  and (edition_type ilike '%dramat%' or edition_type ilike '%full cast%' or edition_type ilike '%graphic audio%')
  and work_title not ilike '%(Dramatized Adaptation)%';

-- Library rows cache the display title too; keep them aligned with the canonical edition.
update user_library_books lb
set title = e.work_title,
    author = coalesce(e.author, lb.author)
from audiobook_editions e
where lb.edition_id = e.id
  and e.work_title is not null;
