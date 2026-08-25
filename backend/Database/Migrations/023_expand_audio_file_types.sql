alter table audiobook_editions
    alter column file_type type varchar(255);

alter table conversion_consents
    alter column file_type type varchar(255);
