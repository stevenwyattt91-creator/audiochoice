-- Repair display metadata only; fingerprints and edition identity remain unchanged.
update audiobook_editions
set work_title = case
    when lower(work_title) like '%fourth wing%' and (part_number = 1 or lower(work_title) ~ 'part[[:space:]]*1[[:space:]]*(of|/)[[:space:]]*2')
      then 'Fourth Wing (Part 1 of 2) (Dramatized Adaptation)'
    when lower(work_title) like '%fourth wing%' and (part_number = 2 or lower(work_title) ~ 'part[[:space:]]*2[[:space:]]*(of|/)[[:space:]]*2')
      then 'Fourth Wing (Part 2 of 2) (Dramatized Adaptation)'
    else work_title
  end,
  edition_type = case
    when lower(work_title) like '%fourth wing%' then 'Dramatized Adaptation'
    else edition_type
  end
where lower(work_title) like '%fourth wing%';

update user_library_books lb
set title = e.work_title,
    author = coalesce(e.author, lb.author)
from audiobook_editions e
where lb.edition_id = e.id
  and lower(e.work_title) like '%fourth wing%';
