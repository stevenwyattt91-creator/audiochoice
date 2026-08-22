alter table scan_jobs add column progress_percent integer not null default 0;
alter table scan_jobs add column progress_stage varchar(40);
alter table scan_jobs add constraint scan_jobs_progress_range
    check (progress_percent >= 0 and progress_percent <= 100);
