create table internal_portal_users (
    user_id uuid primary key references users(id) on delete cascade,
    role varchar(20) not null check (role in ('admin', 'auditor')),
    active boolean not null default true,
    compensation_visible boolean not null default false,
    approved_by uuid references users(id),
    approved_at timestamptz not null default now(),
    updated_at timestamptz not null default now()
);

create table audit_filter_categories (
    id uuid primary key,
    name varchar(100) not null,
    description varchar(500),
    active boolean not null default true,
    display_order integer not null default 0
);

insert into audit_filter_categories(id, name, description, display_order) values
('10000000-0000-0000-0000-000000000001', 'Sexual Content', 'Sexual or intimate content.', 10),
('20000000-0000-0000-0000-000000000001', 'Profanity', 'Profane words or phrases.', 20),
('30000000-0000-0000-0000-000000000001', 'Violence', 'Violent or graphic injury content.', 30),
('40000000-0000-0000-0000-000000000001', 'Drugs & Alcohol', 'Alcohol, intoxication, or drug-related content.', 40),
('50000000-0000-0000-0000-000000000001', 'Blasphemy', 'Religious profanity or blasphemous statements.', 50),
('60000000-0000-0000-0000-000000000001', 'Self-Harm & Suicide', 'Self-harm or suicide-related content.', 60)
on conflict (id) do nothing;

create table audit_assignments (
    id uuid primary key,
    edition_id uuid not null references audiobook_editions(id),
    scan_result_id uuid not null references scan_results(id),
    auditor_id uuid references users(id),
    status varchar(30) not null default 'available'
        check (status in ('available', 'in_progress', 'completed', 'needs_review', 'approved', 'rejected')),
    blind_qc boolean not null default false,
    assigned_at timestamptz,
    started_at timestamptz,
    completed_at timestamptz,
    reviewed_by uuid references users(id),
    reviewed_at timestamptz,
    compensation_amount numeric(10,2),
    payment_status varchar(20) not null default 'unpaid'
        check (payment_status in ('unpaid', 'paid')),
    payment_date date,
    payment_note varchar(1000),
    created_by uuid not null references users(id),
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now()
);
create index audit_assignments_auditor on audit_assignments(auditor_id, status, updated_at desc);
create index audit_assignments_available on audit_assignments(status, created_at)
    where status = 'available';

create unique index audit_assignment_single_standard_review
    on audit_assignments(scan_result_id)
    where blind_qc = false and status not in ('rejected');

create table audit_decisions (
    id uuid primary key,
    assignment_id uuid not null references audit_assignments(id) on delete cascade,
    scan_event_id uuid not null references scan_events(id),
    auditor_id uuid not null references users(id),
    decision varchar(30) not null
        check (decision in ('accurate', 'adjust_timestamps', 'wrong_category', 'false_positive', 'needs_escalation')),
    corrected_category_id uuid references audit_filter_categories(id),
    corrected_start_seconds double precision,
    corrected_end_seconds double precision,
    notes varchar(4000),
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    unique (assignment_id, scan_event_id),
    check (corrected_start_seconds is null or corrected_start_seconds >= 0),
    check (corrected_end_seconds is null or corrected_end_seconds >= corrected_start_seconds)
);
create index audit_decisions_assignment on audit_decisions(assignment_id, updated_at);
create index audit_decisions_escalated on audit_decisions(decision)
    where decision = 'needs_escalation';

create table approved_scan_events (
    id uuid primary key,
    edition_id uuid not null references audiobook_editions(id),
    source_scan_event_id uuid not null references scan_events(id),
    source_audit_decision_id uuid not null references audit_decisions(id),
    category_id uuid not null references audit_filter_categories(id),
    start_seconds double precision not null check (start_seconds >= 0),
    end_seconds double precision not null check (end_seconds >= start_seconds),
    approved_by uuid not null references users(id),
    approved_at timestamptz not null default now(),
    verification_level varchar(30) not null
        check (verification_level in ('human_verified', 'fully_audited')),
    active boolean not null default true
);
create index approved_scan_events_edition on approved_scan_events(edition_id, start_seconds);

create table audit_review_media (
    id uuid primary key,
    edition_id uuid not null references audiobook_editions(id),
    object_name varchar(500) not null,
    context_start_seconds double precision not null,
    context_end_seconds double precision not null,
    created_at timestamptz not null default now(),
    expires_at timestamptz,
    active boolean not null default true,
    check (context_start_seconds >= 0 and context_end_seconds > context_start_seconds)
);

create table internal_audit_log (
    id bigserial primary key,
    actor_id uuid references users(id),
    action varchar(100) not null,
    entity_type varchar(100) not null,
    entity_id varchar(100) not null,
    old_value jsonb,
    new_value jsonb,
    created_at timestamptz not null default now()
);
create index internal_audit_log_entity on internal_audit_log(entity_type, entity_id, created_at desc);
create index internal_audit_log_actor on internal_audit_log(actor_id, created_at desc);
