delete from conversion_consents old
using conversion_consents current
where old.fingerprint_version = 0
  and current.fingerprint_version = 1
  and old.user_id = current.user_id
  and old.sha256 = current.sha256
  and old.file_size = current.file_size;

update conversion_consents set fingerprint_version = 1 where fingerprint_version = 0;
update audiobook_editions old
set fingerprint_version = 1
where old.fingerprint_version = 0
  and not exists (
      select 1 from audiobook_editions current
      where current.fingerprint_version = 1
        and current.sha256 = old.sha256
        and current.file_size = old.file_size
  );
