-- Keep the newest/most complete Iron Flame part-2 edition published. This is
-- deliberately scoped to Iron Flame and cannot hide the other catalog books.
with ranked as (
    select id,
           row_number() over (
             partition by lower(regexp_replace(btrim(work_title), '[^a-z0-9]+', '', 'gi')),
                          lower(coalesce(author, '')), part_number, total_parts
             order by (cover_image is not null) desc, created_at desc, id desc) as rn
    from audiobook_editions
    where explore_published = true
      and lower(work_title) like '%iron flame%'
      and part_number = 2 and total_parts = 2
)
update audiobook_editions e
set explore_published = false
from ranked r
where e.id = r.id and r.rn > 1;
