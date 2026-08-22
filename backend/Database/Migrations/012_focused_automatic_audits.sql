-- A focused review task is created for new scans only when there is sexual,
-- nudity, or graphic-violence material to review. Original audio is not stored.
alter table audit_assignments add column if not exists auto_generated boolean not null default false;
alter table audit_assignments add column if not exists review_focus varchar(200) not null default 'All detected events';
alter table audit_assignments alter column created_by drop not null;

create index if not exists audit_assignments_automatic_queue
    on audit_assignments(status, created_at desc)
    where auto_generated and status = 'available';
