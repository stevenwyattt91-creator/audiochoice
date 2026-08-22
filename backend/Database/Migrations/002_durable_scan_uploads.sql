alter table scan_uploads
    add column upload_token_hash char(64) not null;

create index scan_uploads_owner on scan_uploads(owner_user_id, created_at desc);
create index scan_jobs_owner on scan_jobs(id, upload_id);
