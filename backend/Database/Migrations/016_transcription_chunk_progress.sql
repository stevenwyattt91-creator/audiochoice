alter table scan_jobs
    add column if not exists completed_chunks integer not null default 0,
    add column if not exists total_chunks integer not null default 0;

alter table scan_jobs
    add constraint scan_jobs_chunk_progress_range
    check (completed_chunks >= 0 and total_chunks >= 0 and completed_chunks <= total_chunks);
