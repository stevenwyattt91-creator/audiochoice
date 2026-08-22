create table scan_job_subscribers (
    scan_job_id uuid not null references scan_jobs(id) on delete cascade,
    user_id uuid not null references users(id) on delete cascade,
    created_at timestamptz not null,
    primary key (scan_job_id, user_id)
);

insert into scan_job_subscribers(scan_job_id, user_id, created_at)
select j.id, u.owner_user_id, j.created_at
from scan_jobs j
join scan_uploads u on u.id = j.upload_id
on conflict do nothing;

create index scan_job_subscribers_user on scan_job_subscribers(user_id, created_at desc);
