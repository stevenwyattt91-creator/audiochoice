-- Additive isolation: existing jobs remain with the Azure/OpenAI worker.
alter table scan_jobs
    add column if not exists processing_lane varchar(40) not null default 'azure-openai';

create index if not exists scan_jobs_lane_dequeue
    on scan_jobs(processing_lane, status, available_at);
