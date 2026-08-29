-- Labels the imported Fourth Wing Part 2 row, which arrived as a plain "Fourth Wing".
--
-- The same repair as 022, for the other half of the release. That migration claimed the
-- Part 1 row by its eight-hour runtime and renamed it, so any row still titled plainly is
-- not Part 1. Matched on the plain title, the author, and the absence of a part number
-- rather than on a runtime, because Part 2's is not known here; the Part 1 band is excluded
-- anyway so that a freshly imported Part 1 cannot be caught by this.
--
-- Setting the structured columns is what matters. EditionTitleFormatter strips part and
-- edition wording from a stored title and re-appends it from these, so the entry reads
-- "Fourth Wing (Part 2 of 2) (Dramatized Adaptation)" wherever it is displayed.
update audiobook_editions
set work_title = 'Fourth Wing (Part 2 of 2) (Dramatized Adaptation)',
    edition_type = 'Dramatized Adaptation',
    part_number = 2,
    total_parts = 2
where lower(btrim(work_title)) = 'fourth wing'
  and lower(coalesce(author, '')) = 'rebecca yarros'
  and part_number is null
  and (duration_seconds is null or duration_seconds not between 28790 and 28810);

-- Listeners who already hold it see the corrected name too. Restricted to rows still
-- carrying the plain title, so anyone who renamed the book themselves keeps their own name:
-- 022 overwrote on the edition match alone and would have discarded such a correction.
update user_library_books lb
set title = e.work_title,
    author = coalesce(e.author, lb.author)
from audiobook_editions e
where lb.edition_id = e.id
  and e.work_title = 'Fourth Wing (Part 2 of 2) (Dramatized Adaptation)'
  and e.part_number = 2
  and lower(btrim(lb.title)) = 'fourth wing';
