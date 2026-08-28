-- The publisher's synopsis, read from the audiobook file's own `ldes`/`©des` atoms by
-- the client that imported it.
--
-- Explore had no per-edition description at all: five titles carried hand-written prose
-- in code and every other book was given a generated line about AudioChoice itself under
-- a heading that reads "About this audiobook". Storing what the file already says makes
-- that heading honest without asking an outside metadata service.
--
-- It belongs on the edition rather than on a listener's library row because it describes
-- the recording, exactly like the cover image alongside it.
alter table audiobook_editions
    add column if not exists description varchar(4000);
