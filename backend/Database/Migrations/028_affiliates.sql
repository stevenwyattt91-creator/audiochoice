create table affiliates (
    id uuid primary key,
    code varchar(40) not null,
    label varchar(200) not null,
    email varchar(254),
    active boolean not null default true,
    created_at timestamptz not null default now()
);
-- Case-insensitive so "SUMMER10" and "summer10" cannot be issued as two different codes.
create unique index affiliates_code on affiliates(lower(code));

create table affiliate_referrals (
    user_id uuid primary key references users(id) on delete cascade,
    affiliate_id uuid not null references affiliates(id),
    attributed_at timestamptz not null default now()
);
create index affiliate_referrals_affiliate on affiliate_referrals(affiliate_id, attributed_at desc);
