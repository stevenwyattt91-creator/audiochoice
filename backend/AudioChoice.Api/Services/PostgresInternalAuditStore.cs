#if POSTGRES
using System.Text.Json;
using AudioChoice.Api.Contracts;
using Npgsql;

namespace AudioChoice.Api.Services;

public sealed class PostgresInternalAuditStore(NpgsqlDataSource dataSource) : IInternalAuditStore
{
    public InternalAccess? Access(Guid userID)
    {
        using var c = dataSource.OpenConnection();
        using var q = new NpgsqlCommand("""
            select u.id,u.email,u.display_name,p.role,p.active from internal_portal_users p
            join users u on u.id=p.user_id where p.user_id=$1
            """, c); q.Parameters.AddWithValue(userID);
        using var r=q.ExecuteReader(); return r.Read()?ReadAccess(r):null;
    }

    public bool ClaimInitialAdmin(Guid userID)
    {
        using var c = dataSource.OpenConnection();
        using var tx = c.BeginTransaction();
        using (var gate = new NpgsqlCommand("select pg_advisory_xact_lock(731904);", c, tx)) gate.ExecuteNonQuery();
        using (var exists = new NpgsqlCommand("select exists(select 1 from internal_portal_users where role='admin' and active)", c, tx))
            if ((bool)(exists.ExecuteScalar() ?? false)) return false;
        using var q = new NpgsqlCommand("""
            insert into internal_portal_users(user_id,role,active,compensation_visible,approved_by)
            values($1,'admin',true,true,$1)
            on conflict(user_id) do update set role='admin',active=true,compensation_visible=true,
                approved_by=$1,approved_at=now(),updated_at=now()
            """, c, tx);
        q.Parameters.AddWithValue(userID);
        var ok = q.ExecuteNonQuery() == 1;
        if (ok) Log(c, tx, userID, "access.initial_admin_claimed", "user", userID.ToString(), null, new { role = "admin" });
        tx.Commit();
        return ok;
    }

    public IReadOnlyList<AuditAssignmentSummary> Dashboard(Guid userID, bool admin)
    {
        using var c=dataSource.OpenConnection(); using var q=new NpgsqlCommand("""
            select a.id,coalesce(e.work_title,'Untitled audiobook'),e.author,e.edition_type,
              concat(e.fingerprint_version,':',e.sha256,':',e.file_size),count(se.id),count(d.id),
              a.status,a.completed_at,a.compensation_amount,a.payment_status,a.review_focus,a.review_media_status
            from audit_assignments a join audiobook_editions e on e.id=a.edition_id
            join scan_events se on se.scan_result_id=a.scan_result_id and (not a.auto_generated or se.category_id = any(array['10000000-0000-0000-0000-000000000001','60000000-0000-0000-0000-000000000001','40000000-0000-0000-0000-000000000001']::uuid[]) or se.event_id = any(array['31100000-0000-0000-0000-000000000001','31100000-0000-0000-0000-000000000003','31100000-0000-0000-0000-000000000004','31100000-0000-0000-0000-000000000006','31100000-0000-0000-0000-000000000007']::uuid[]))
            left join audit_decisions d on d.assignment_id=a.id and d.scan_event_id=se.id
            where ($2 or a.auditor_id=$1 or (a.status='available' and a.review_media_status='ready'))
            group by a.id,e.id order by case a.status when 'in_progress' then 0 when 'available' then 1 else 2 end,a.updated_at desc
            """,c); q.Parameters.AddWithValue(userID);q.Parameters.AddWithValue(admin);
        using var r=q.ExecuteReader();var values=new List<AuditAssignmentSummary>();while(r.Read())values.Add(ReadSummary(r));return values;
    }

    public AuditorEarnings Earnings(Guid userID)
    {
        using var c=dataSource.OpenConnection();using var q=new NpgsqlCommand("""
            select
              coalesce(sum(compensation_amount) filter(where payment_status='paid' and payment_date >= date_trunc('week',current_date)::date),0),
              coalesce(sum(compensation_amount) filter(where status in ('completed','needs_review')),0),
              coalesce(sum(compensation_amount) filter(where status='approved' and payment_status='unpaid'),0),
              coalesce(sum(compensation_amount) filter(where payment_status='paid' and payment_date >= date_trunc('week',current_date)::date),0)
            from audit_assignments where auditor_id=$1
            """,c);q.Parameters.AddWithValue(userID);using var r=q.ExecuteReader();return r.Read()?new(r.GetDecimal(0),r.GetDecimal(1),r.GetDecimal(2),r.GetDecimal(3)):new(0,0,0,0);
    }

    public AuditWorkspace? Workspace(Guid assignmentID, Guid userID, bool admin)
    {
        using var c=dataSource.OpenConnection();
        if(!CanUse(c,assignmentID,userID,admin))return null;
        AuditAssignmentSummary summary;
        using(var q=new NpgsqlCommand("""
            select a.id,coalesce(e.work_title,'Untitled audiobook'),e.author,e.edition_type,
              concat(e.fingerprint_version,':',e.sha256,':',e.file_size),count(se.id),count(d.id),
              a.status,a.completed_at,a.compensation_amount,a.payment_status,a.review_focus,a.review_media_status
            from audit_assignments a join audiobook_editions e on e.id=a.edition_id
            join scan_events se on se.scan_result_id=a.scan_result_id and (not a.auto_generated or se.category_id = any(array['10000000-0000-0000-0000-000000000001','60000000-0000-0000-0000-000000000001','40000000-0000-0000-0000-000000000001']::uuid[]) or se.event_id = any(array['31100000-0000-0000-0000-000000000001','31100000-0000-0000-0000-000000000003','31100000-0000-0000-0000-000000000004','31100000-0000-0000-0000-000000000006','31100000-0000-0000-0000-000000000007']::uuid[]))
            left join audit_decisions d on d.assignment_id=a.id and d.scan_event_id=se.id
            where a.id=$1 group by a.id,e.id
            """,c)){q.Parameters.AddWithValue(assignmentID);using var r=q.ExecuteReader();if(!r.Read())return null;summary=ReadSummary(r);}
        var categories=new List<AuditCategory>();using(var q=new NpgsqlCommand("select id,name,description from audit_filter_categories where active order by display_order,name",c)){using var r=q.ExecuteReader();while(r.Read())categories.Add(new(r.GetGuid(0),r.GetString(1),r.IsDBNull(2)?null:r.GetString(2)));}
        var candidates=new List<AuditCandidate>();using(var q=new NpgsqlCommand("""
            select se.id,se.start_seconds,se.end_seconds,se.category_id,se.confidence,se.safe_description,se.stable_key,greatest(0,se.start_seconds-15),se.end_seconds+15
            from audit_assignments a join scan_events se on se.scan_result_id=a.scan_result_id
            where a.id=$1 and (not a.auto_generated or se.category_id = any(array['10000000-0000-0000-0000-000000000001','60000000-0000-0000-0000-000000000001','40000000-0000-0000-0000-000000000001']::uuid[]) or se.event_id = any(array['31100000-0000-0000-0000-000000000001','31100000-0000-0000-0000-000000000003','31100000-0000-0000-0000-000000000004','31100000-0000-0000-0000-000000000006','31100000-0000-0000-0000-000000000007']::uuid[])) order by se.start_seconds,se.id
            """,c)){q.Parameters.AddWithValue(assignmentID);using var r=q.ExecuteReader();while(r.Read())candidates.Add(new(r.GetGuid(0),r.GetDouble(1),r.GetDouble(2),r.GetGuid(3),r.GetDouble(4),r.GetString(5),r.GetString(6).Trim(),r.GetDouble(7),r.GetDouble(8)));}
        var decisions=new List<AuditDecisionRecord>();using(var q=new NpgsqlCommand("select id,scan_event_id,decision,corrected_category_id,corrected_start_seconds,corrected_end_seconds,notes,updated_at from audit_decisions where assignment_id=$1",c)){q.Parameters.AddWithValue(assignmentID);using var r=q.ExecuteReader();while(r.Read())decisions.Add(ReadDecision(r));}
        using var media=new NpgsqlCommand("select exists(select 1 from audit_review_sources where assignment_id=$1 and deleted_at is null)",c);media.Parameters.AddWithValue(assignmentID);
        return new(summary,categories,candidates,decisions,(bool)(media.ExecuteScalar()??false));
    }

    public bool Claim(Guid assignmentID,Guid userID){using var c=dataSource.OpenConnection();using var q=new NpgsqlCommand("update audit_assignments set auditor_id=$1,status='in_progress',assigned_at=now(),started_at=now(),updated_at=now() where id=$2 and status='available' and review_media_status='ready' and auditor_id is null",c);q.Parameters.AddWithValue(userID);q.Parameters.AddWithValue(assignmentID);return q.ExecuteNonQuery()==1;}

    public AuditDecisionRecord? SaveDecision(Guid assignmentID,Guid candidateID,Guid userID,bool admin,AuditDecisionRequest request)
    {
        var allowed=new[]{"accurate","adjust_timestamps","wrong_category","false_positive","needs_escalation"};if(!allowed.Contains(request.Decision))return null;
        using var c=dataSource.OpenConnection();if(!CanUse(c,assignmentID,userID,admin))return null;using var tx=c.BeginTransaction();
        object? old;using(var o=new NpgsqlCommand("select to_jsonb(d) from audit_decisions d where assignment_id=$1 and scan_event_id=$2",c,tx)){o.Parameters.AddWithValue(assignmentID);o.Parameters.AddWithValue(candidateID);old=o.ExecuteScalar();}
        using var q=new NpgsqlCommand("""
            insert into audit_decisions(id,assignment_id,scan_event_id,auditor_id,decision,corrected_category_id,corrected_start_seconds,corrected_end_seconds,notes)
            select gen_random_uuid(),$1,$2,$3,$4,$5,$6,$7,$8 where exists(select 1 from audit_assignments a join scan_events se on se.scan_result_id=a.scan_result_id where a.id=$1 and se.id=$2 and (not a.auto_generated or se.category_id = any(array['10000000-0000-0000-0000-000000000001','60000000-0000-0000-0000-000000000001','40000000-0000-0000-0000-000000000001']::uuid[]) or se.event_id = any(array['31100000-0000-0000-0000-000000000001','31100000-0000-0000-0000-000000000003','31100000-0000-0000-0000-000000000004','31100000-0000-0000-0000-000000000006','31100000-0000-0000-0000-000000000007']::uuid[])))
            on conflict(assignment_id,scan_event_id) do update set decision=excluded.decision,corrected_category_id=excluded.corrected_category_id,corrected_start_seconds=excluded.corrected_start_seconds,corrected_end_seconds=excluded.corrected_end_seconds,notes=excluded.notes,updated_at=now()
            returning id,scan_event_id,decision,corrected_category_id,corrected_start_seconds,corrected_end_seconds,notes,updated_at
            """,c,tx);q.Parameters.AddWithValue(assignmentID);q.Parameters.AddWithValue(candidateID);q.Parameters.AddWithValue(userID);q.Parameters.AddWithValue(request.Decision);q.Parameters.AddWithValue((object?)request.CorrectedCategoryID??DBNull.Value);q.Parameters.AddWithValue((object?)request.CorrectedStartSeconds??DBNull.Value);q.Parameters.AddWithValue((object?)request.CorrectedEndSeconds??DBNull.Value);q.Parameters.AddWithValue((object?)request.Notes?.Trim()??DBNull.Value);using var r=q.ExecuteReader();if(!r.Read())return null;var value=ReadDecision(r);r.Close();Log(c,tx,userID,"decision.saved","audit_decision",value.ID.ToString(),old,value);tx.Commit();return value;
    }

    public bool Complete(Guid id,Guid userID,bool admin){using var c=dataSource.OpenConnection();using var q=new NpgsqlCommand("""
        update audit_assignments a set status=case when exists(select 1 from audit_decisions d where d.assignment_id=a.id and d.decision='needs_escalation') then 'needs_review' else 'completed' end,
        compensation_amount=a.compensation_amount,
        completed_at=now(),updated_at=now()
        where a.id=$1 and ($3 or a.auditor_id=$2) and (select count(*) from scan_events se where se.scan_result_id=a.scan_result_id and (not a.auto_generated or se.category_id = any(array['10000000-0000-0000-0000-000000000001','60000000-0000-0000-0000-000000000001','40000000-0000-0000-0000-000000000001']::uuid[]) or se.event_id = any(array['31100000-0000-0000-0000-000000000001','31100000-0000-0000-0000-000000000003','31100000-0000-0000-0000-000000000004','31100000-0000-0000-0000-000000000006','31100000-0000-0000-0000-000000000007']::uuid[])))=(select count(*) from audit_decisions d where d.assignment_id=a.id)
        """,c);q.Parameters.AddWithValue(id);q.Parameters.AddWithValue(userID);q.Parameters.AddWithValue(admin);return q.ExecuteNonQuery()==1;}
    public IReadOnlyList<InternalAccess> Users(){using var c=dataSource.OpenConnection();using var q=new NpgsqlCommand("select u.id,u.email,u.display_name,p.role,p.active from internal_portal_users p join users u on u.id=p.user_id order by u.display_name",c);using var r=q.ExecuteReader();var x=new List<InternalAccess>();while(r.Read())x.Add(ReadAccess(r));return x;}
    public bool Grant(Guid actor,Guid user,string role){if(role is not("admin" or "auditor"))return false;using var c=dataSource.OpenConnection();using var tx=c.BeginTransaction();using var q=new NpgsqlCommand("insert into internal_portal_users(user_id,role,approved_by) values($1,$2,$3) on conflict(user_id) do update set role=excluded.role,active=true,approved_by=excluded.approved_by,approved_at=now(),updated_at=now()",c,tx);q.Parameters.AddWithValue(user);q.Parameters.AddWithValue(role);q.Parameters.AddWithValue(actor);var ok=q.ExecuteNonQuery()==1;Log(c,tx,actor,"access.granted","user",user.ToString(),null,new{role});tx.Commit();return ok;}
    public bool SetActive(Guid actor,Guid user,bool active){using var c=dataSource.OpenConnection();using var tx=c.BeginTransaction();using var q=new NpgsqlCommand("update internal_portal_users set active=$1,updated_at=now() where user_id=$2",c,tx);q.Parameters.AddWithValue(active);q.Parameters.AddWithValue(user);var ok=q.ExecuteNonQuery()==1;if(!active){using var s=new NpgsqlCommand("delete from user_sessions where user_id=$1",c,tx);s.Parameters.AddWithValue(user);s.ExecuteNonQuery();}Log(c,tx,actor,active?"access.enabled":"access.disabled","user",user.ToString(),null,new{active});tx.Commit();return ok;}
    public Guid? CreateAssignment(Guid actor,CreateAuditAssignmentRequest r){using var c=dataSource.OpenConnection();using var tx=c.BeginTransaction();using var q=new NpgsqlCommand("insert into audit_assignments(id,edition_id,scan_result_id,auditor_id,status,blind_qc,assigned_at,compensation_amount,created_by,review_media_status) select gen_random_uuid(),edition_id,id,$2,case when $2 is null then 'available' else 'in_progress' end,$3,case when $2 is null then null else now() end,$4,$5,'waiting_for_source' from scan_results where id=$1 returning id",c,tx);q.Parameters.AddWithValue(r.ScanResultID);q.Parameters.AddWithValue((object?)r.AuditorID??DBNull.Value);q.Parameters.AddWithValue(r.BlindQC);q.Parameters.AddWithValue((object?)r.CompensationAmount??DBNull.Value);q.Parameters.AddWithValue(actor);var id=q.ExecuteScalar() as Guid?;if(id!=null)Log(c,tx,actor,"assignment.created","audit_assignment",id.ToString()!,null,r);tx.Commit();return id;}
    public bool CreateAutomaticFocusedAssignment(Guid scanJobID)
    {
        using var c=dataSource.OpenConnection();
        Guid editionID, scanResultID;
        using (var source = new NpgsqlCommand("""
            select r.edition_id,r.id from scan_jobs j join scan_results r on r.edition_id=j.edition_id
            where j.id=$1 and r.scanned_at=(select max(r2.scanned_at) from scan_results r2 where r2.edition_id=j.edition_id)
              and exists(select 1 from scan_events se where se.scan_result_id=r.id and (se.category_id = any(array['10000000-0000-0000-0000-000000000001','60000000-0000-0000-0000-000000000001','40000000-0000-0000-0000-000000000001']::uuid[]) or se.event_id = any(array['31100000-0000-0000-0000-000000000001','31100000-0000-0000-0000-000000000003','31100000-0000-0000-0000-000000000004','31100000-0000-0000-0000-000000000006','31100000-0000-0000-0000-000000000007']::uuid[])))
            """,c))
        { source.Parameters.AddWithValue(scanJobID); using var reader=source.ExecuteReader(); if(!reader.Read())return false; editionID=reader.GetGuid(0);scanResultID=reader.GetGuid(1); }
        var compensation=FocusedCompensation(c,scanResultID);
        using var q=new NpgsqlCommand("""
            insert into audit_assignments(id,edition_id,scan_result_id,status,blind_qc,auto_generated,review_focus,compensation_amount,review_media_status)
            values(gen_random_uuid(),$1,$2,'available',false,true,'Sexual content, sexual assault, self-harm, graphic violence, torture, child violence, and animal violence',$3,'waiting_for_source')
            on conflict do nothing
            """,c);q.Parameters.AddWithValue(editionID);q.Parameters.AddWithValue(scanResultID);q.Parameters.AddWithValue(compensation);return q.ExecuteNonQuery()==1;
    }

    public IReadOnlyList<Guid> DeleteAllAssignments()
    {
        using var c = dataSource.OpenConnection();
        using var tx = c.BeginTransaction();
        var ids = new List<Guid>();
        using (var read = new NpgsqlCommand("select id from audit_assignments", c, tx))
        using (var reader = read.ExecuteReader()) while (reader.Read()) ids.Add(reader.GetGuid(0));
        using (var delete = new NpgsqlCommand("delete from audit_assignments", c, tx)) delete.ExecuteNonQuery();
        tx.Commit();
        return ids;
    }

    public Guid? CreateFocusedAssignmentForExploreCatalog(string catalogID)
    {
        if (string.IsNullOrWhiteSpace(catalogID)) return null;
        using var c=dataSource.OpenConnection();using var tx=c.BeginTransaction();
        Guid editionID, scanResultID;
        using(var source=new NpgsqlCommand("""
            select e.id,r.id from audiobook_editions e
            join lateral (select id from scan_results where edition_id=e.id order by scanned_at desc limit 1) r on true
            where e.explore_published=true and left(lower(e.sha256),24)=$1
            """,c,tx))
        {source.Parameters.AddWithValue(catalogID.Trim().ToLowerInvariant());using var reader=source.ExecuteReader();if(!reader.Read())return null;editionID=reader.GetGuid(0);scanResultID=reader.GetGuid(1);}
        using(var existing=new NpgsqlCommand("select id from audit_assignments where scan_result_id=$1 and auto_generated=true limit 1",c,tx))
        {existing.Parameters.AddWithValue(scanResultID);var id=existing.ExecuteScalar() as Guid?;if(id is not null)return id;}
        var compensation=FocusedCompensation(c,scanResultID);
        using var q=new NpgsqlCommand("""
            insert into audit_assignments(id,edition_id,scan_result_id,status,blind_qc,auto_generated,review_focus,compensation_amount,review_media_status)
            values(gen_random_uuid(),$1,$2,'available',false,true,'Sexual content, sexual assault, self-harm, graphic violence, torture, child violence, and animal violence',$3,'waiting_for_source')
            returning id
            """,c,tx);q.Parameters.AddWithValue(editionID);q.Parameters.AddWithValue(scanResultID);q.Parameters.AddWithValue(compensation);
        var assignmentID=q.ExecuteScalar() as Guid?;tx.Commit();return assignmentID;
    }

    public AdminDashboardSummary AdminDashboard()
    {
        using var c=dataSource.OpenConnection();using var q=new NpgsqlCommand("""
            select
              (select count(*)::int from audiobook_editions e where exists(select 1 from scan_results r where r.edition_id=e.id)),
              (select count(*)::int from audit_assignments where status in ('completed','needs_review')),
              (select count(*)::int from audit_assignments where status='approved' and payment_status='unpaid'),
              (select coalesce(sum(compensation_amount),0) from audit_assignments where status='approved' and payment_status='unpaid'),
              (select count(*)::int from internal_portal_users where role='auditor' and active)
            """,c);using var r=q.ExecuteReader();return r.Read()?new(r.GetInt32(0),r.GetInt32(1),r.GetInt32(2),r.GetDecimal(3),r.GetInt32(4)):new(0,0,0,0,0);
    }

    public IReadOnlyList<AdminCatalogEditionSummary> Catalog(string? search)
    {
        using var c=dataSource.OpenConnection();using var q=new NpgsqlCommand("""
            select e.id,r.id,e.fingerprint_version,e.sha256,e.file_size,e.duration_seconds,e.file_type,e.work_title,e.author,e.series_title,e.series_number,e.edition_type,e.part_number,e.total_parts,
                   r.scanned_at,r.scanner_version,(select count(*)::int from scan_events se where se.scan_result_id=r.id),e.explore_published,e.cover_image is not null
            from audiobook_editions e join lateral (select id,scanned_at,scanner_version from scan_results where edition_id=e.id order by scanned_at desc limit 1) r on true
            where $1='' or lower(concat_ws(' ',e.work_title,e.author,e.series_title,e.edition_type,e.file_type)) like '%' || lower($1) || '%'
            order by coalesce(e.work_title,'Untitled audiobook'),r.scanned_at desc
            """,c);q.Parameters.AddWithValue(search?.Trim()??string.Empty);using var r=q.ExecuteReader();var values=new List<AdminCatalogEditionSummary>();while(r.Read())values.Add(ReadCatalog(r));return values;
    }

    public AdminCatalogEditionSummary? CatalogEdition(Guid editionID)
    {
        using var c=dataSource.OpenConnection();using var q=new NpgsqlCommand("""
            select e.id,r.id,e.fingerprint_version,e.sha256,e.file_size,e.duration_seconds,e.file_type,e.work_title,e.author,e.series_title,e.series_number,e.edition_type,e.part_number,e.total_parts,
                   r.scanned_at,r.scanner_version,(select count(*)::int from scan_events se where se.scan_result_id=r.id),e.explore_published,e.cover_image is not null
            from audiobook_editions e join lateral (select id,scanned_at,scanner_version from scan_results where edition_id=e.id order by scanned_at desc limit 1) r on true
            where e.id=$1
            """,c);q.Parameters.AddWithValue(editionID);using var r=q.ExecuteReader();return r.Read()?ReadCatalog(r):null;
    }

    public IReadOnlyList<AdminAuditPayment> Payments()
    {
        using var c=dataSource.OpenConnection();using var q=new NpgsqlCommand("""
            select a.id,a.auditor_id,u.display_name,u.email,coalesce(e.work_title,'Untitled audiobook'),e.edition_type,a.status,
              coalesce(a.compensation_amount,0),a.payment_status,a.completed_at,a.payment_date,a.payment_note
            from audit_assignments a join audiobook_editions e on e.id=a.edition_id left join users u on u.id=a.auditor_id
            where a.status='approved' or a.payment_status='paid'
            order by case when a.payment_status='unpaid' then 0 else 1 end,a.completed_at desc nulls last
            """,c);using var r=q.ExecuteReader();var values=new List<AdminAuditPayment>();while(r.Read())values.Add(new(r.GetGuid(0),r.IsDBNull(1)?null:r.GetGuid(1),r.IsDBNull(2)?null:r.GetString(2),r.IsDBNull(3)?null:r.GetString(3),r.GetString(4),r.IsDBNull(5)?null:r.GetString(5),r.GetString(6),r.GetDecimal(7),r.GetString(8),r.IsDBNull(9)?null:r.GetFieldValue<DateTimeOffset>(9),r.IsDBNull(10)?null:DateOnly.FromDateTime(r.GetDateTime(10)),r.IsDBNull(11)?null:r.GetString(11)));return values;
    }

    public bool ApproveAssignment(Guid actor,Guid assignmentID)=>SetAssignmentStatus(actor,assignmentID,"approved",new[]{"completed","needs_review"});

    public AuditReviewSource? ReviewSource(Guid assignmentID)
    {
        using var c=dataSource.OpenConnection();using var q=new NpgsqlCommand("select object_name,original_file_name,content_type,file_size from audit_review_sources where assignment_id=$1 and deleted_at is null",c);q.Parameters.AddWithValue(assignmentID);using var r=q.ExecuteReader();return r.Read()?new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetInt64(3)):null;
    }
    public AuditReviewClip? ReviewClip(Guid assignmentID,Guid candidateID)
    {
        using var c=dataSource.OpenConnection();using var q=new NpgsqlCommand("select object_name,context_start_seconds,context_end_seconds from audit_review_clips where assignment_id=$1 and scan_event_id=$2 and (delete_after is null or delete_after>now())",c);q.Parameters.AddWithValue(assignmentID);q.Parameters.AddWithValue(candidateID);using var r=q.ExecuteReader();return r.Read()?new(r.GetString(0),r.GetDouble(1),r.GetDouble(2)):null;
    }
    public bool SaveReviewSource(Guid actor,Guid assignmentID,AuditReviewSource source)
    {
        using var c=dataSource.OpenConnection();using var tx=c.BeginTransaction();using var q=new NpgsqlCommand("insert into audit_review_sources(assignment_id,object_name,original_file_name,content_type,file_size,uploaded_by) values($1,$2,$3,$4,$5,$6) on conflict(assignment_id) do update set object_name=excluded.object_name,original_file_name=excluded.original_file_name,content_type=excluded.content_type,file_size=excluded.file_size,uploaded_by=excluded.uploaded_by,uploaded_at=now(),delete_after=null,deleted_at=null",c,tx);q.Parameters.AddWithValue(assignmentID);q.Parameters.AddWithValue(source.ObjectName);q.Parameters.AddWithValue(source.OriginalFileName);q.Parameters.AddWithValue(source.ContentType);q.Parameters.AddWithValue(source.FileSize);q.Parameters.AddWithValue(actor);var ok=q.ExecuteNonQuery()==1;if(ok){using var ready=new NpgsqlCommand("update audit_assignments set review_media_status='ready',updated_at=now() where id=$1 and status='available'",c,tx);ready.Parameters.AddWithValue(assignmentID);ok=ready.ExecuteNonQuery()==1;Log(c,tx,actor,"review_media.attached","audit_assignment",assignmentID.ToString(),null,new{source.OriginalFileName,source.FileSize});}tx.Commit();return ok;
    }
    public bool SaveReviewClip(Guid assignmentID,Guid candidateID,AuditReviewClip clip)
    {
        using var c=dataSource.OpenConnection();using var q=new NpgsqlCommand("insert into audit_review_clips(assignment_id,scan_event_id,object_name,context_start_seconds,context_end_seconds) values($1,$2,$3,$4,$5) on conflict(assignment_id,scan_event_id) do update set object_name=excluded.object_name,context_start_seconds=excluded.context_start_seconds,context_end_seconds=excluded.context_end_seconds,created_at=now(),delete_after=null",c);q.Parameters.AddWithValue(assignmentID);q.Parameters.AddWithValue(candidateID);q.Parameters.AddWithValue(clip.ObjectName);q.Parameters.AddWithValue(clip.StartSeconds);q.Parameters.AddWithValue(clip.EndSeconds);return q.ExecuteNonQuery()==1;
    }
    public bool ScheduleReviewMediaCleanup(Guid actor,Guid assignmentID)
    {
        using var c=dataSource.OpenConnection();using var tx=c.BeginTransaction();using var q=new NpgsqlCommand("update audit_assignments set review_media_status='cleanup_pending',updated_at=now() where id=$1 and review_media_status='ready'",c,tx);q.Parameters.AddWithValue(assignmentID);var ok=q.ExecuteNonQuery()==1;if(ok){using var source=new NpgsqlCommand("update audit_review_sources set delete_after=now() where assignment_id=$1 and deleted_at is null",c,tx);source.Parameters.AddWithValue(assignmentID);source.ExecuteNonQuery();using var clips=new NpgsqlCommand("update audit_review_clips set delete_after=now() where assignment_id=$1",c,tx);clips.Parameters.AddWithValue(assignmentID);clips.ExecuteNonQuery();Log(c,tx,actor,"review_media.cleanup_scheduled","audit_assignment",assignmentID.ToString(),null,null);}tx.Commit();return ok;
    }
    public bool SetAssignmentCompensation(Guid actor,Guid assignmentID,decimal amount)
    {
        if(amount<0)return false;
        using var c=dataSource.OpenConnection();using var tx=c.BeginTransaction();
        object? old;using(var read=new NpgsqlCommand("select compensation_amount from audit_assignments where id=$1",c,tx)){read.Parameters.AddWithValue(assignmentID);old=read.ExecuteScalar();}
        using var q=new NpgsqlCommand("update audit_assignments set compensation_amount=$1,updated_at=now() where id=$2 and status in ('completed','needs_review','approved')",c,tx);q.Parameters.AddWithValue(amount);q.Parameters.AddWithValue(assignmentID);var ok=q.ExecuteNonQuery()==1;
        if(ok)Log(c,tx,actor,"assignment.compensation_set","audit_assignment",assignmentID.ToString(),old,new{amount});tx.Commit();return ok;
    }
    public bool RejectAssignment(Guid actor,Guid assignmentID)=>SetAssignmentStatus(actor,assignmentID,"rejected",new[]{"completed","needs_review","approved"});
    public bool MarkAssignmentPaid(Guid actor,Guid assignmentID,string? note)
    {
        using var c=dataSource.OpenConnection();using var tx=c.BeginTransaction();using var q=new NpgsqlCommand("update audit_assignments set payment_status='paid',payment_date=current_date,payment_note=$1,updated_at=now() where id=$2 and status='approved' and payment_status='unpaid'",c,tx);q.Parameters.AddWithValue((object?)note?.Trim()??DBNull.Value);q.Parameters.AddWithValue(assignmentID);var ok=q.ExecuteNonQuery()==1;if(ok)Log(c,tx,actor,"payment.marked_paid","audit_assignment",assignmentID.ToString(),null,new{note});tx.Commit();return ok;
    }

    private static decimal FocusedCompensation(NpgsqlConnection c, Guid scanResultID)
    {
        var events = new List<(double Start, double End)>();
        using var q = new NpgsqlCommand("""
            select start_seconds,end_seconds from scan_events
            where scan_result_id=$1 and (category_id = any(array['10000000-0000-0000-0000-000000000001','60000000-0000-0000-0000-000000000001','40000000-0000-0000-0000-000000000001']::uuid[]) or event_id = any(array['31100000-0000-0000-0000-000000000001','31100000-0000-0000-0000-000000000003','31100000-0000-0000-0000-000000000004','31100000-0000-0000-0000-000000000006','31100000-0000-0000-0000-000000000007']::uuid[]))
            order by start_seconds,end_seconds
            """, c);
        q.Parameters.AddWithValue(scanResultID);
        using var reader = q.ExecuteReader();
        while (reader.Read()) events.Add((reader.GetDouble(0), reader.GetDouble(1)));
        var paymentGroups = (int)Math.Ceiling(events.Count / 7m);
        return Math.Min(10.00m, 0.30m * paymentGroups);
    }

    private bool SetAssignmentStatus(Guid actor,Guid assignmentID,string status,IReadOnlyList<string> allowed)
    {
        using var c=dataSource.OpenConnection();using var tx=c.BeginTransaction();using var q=new NpgsqlCommand("update audit_assignments set status=$1,reviewed_by=$2,reviewed_at=now(),updated_at=now() where id=$3 and status=any($4)",c,tx);q.Parameters.AddWithValue(status);q.Parameters.AddWithValue(actor);q.Parameters.AddWithValue(assignmentID);q.Parameters.AddWithValue(allowed.ToArray());var ok=q.ExecuteNonQuery()==1;if(ok)Log(c,tx,actor,$"assignment.{status}","audit_assignment",assignmentID.ToString(),null,new{status});tx.Commit();return ok;
    }

    private static AdminCatalogEditionSummary ReadCatalog(NpgsqlDataReader r)
    {
        var fingerprint=new BookFingerprint(r.GetInt32(2),r.GetString(3),r.GetInt64(4),r.IsDBNull(5)?null:r.GetDouble(5),r.GetString(6),r.IsDBNull(7)?null:r.GetString(7),r.IsDBNull(8)?null:r.GetString(8),r.IsDBNull(9)?null:r.GetString(9),r.IsDBNull(10)?null:r.GetInt32(10),r.IsDBNull(11)?null:r.GetString(11),r.IsDBNull(12)?null:r.GetInt32(12),r.IsDBNull(13)?null:r.GetInt32(13));
        return new(r.GetGuid(0),r.GetGuid(1),fingerprint,r.GetFieldValue<DateTimeOffset>(14),r.GetString(15),r.GetInt32(16),r.GetBoolean(17),r.GetBoolean(18),false);
    }

    private static bool CanUse(NpgsqlConnection c,Guid id,Guid user,bool admin){using var q=new NpgsqlCommand("select exists(select 1 from audit_assignments where id=$1 and ($3 or auditor_id=$2))",c);q.Parameters.AddWithValue(id);q.Parameters.AddWithValue(user);q.Parameters.AddWithValue(admin);return (bool)(q.ExecuteScalar()??false);}
    private static InternalAccess ReadAccess(NpgsqlDataReader r)=>new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetBoolean(4));
    private static AuditAssignmentSummary ReadSummary(NpgsqlDataReader r)=>new(r.GetGuid(0),r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),r.IsDBNull(3)?null:r.GetString(3),r.GetString(4),Convert.ToInt32(r.GetInt64(5)),Convert.ToInt32(r.GetInt64(6)),r.GetString(7),r.IsDBNull(8)?null:r.GetFieldValue<DateTimeOffset>(8),r.IsDBNull(9)?null:r.GetDecimal(9),r.GetString(10),r.GetString(11),r.GetString(12));
    private static AuditDecisionRecord ReadDecision(NpgsqlDataReader r)=>new(r.GetGuid(0),r.GetGuid(1),r.GetString(2),r.IsDBNull(3)?null:r.GetGuid(3),r.IsDBNull(4)?null:r.GetDouble(4),r.IsDBNull(5)?null:r.GetDouble(5),r.IsDBNull(6)?null:r.GetString(6),r.GetFieldValue<DateTimeOffset>(7));
    private static void Log(NpgsqlConnection c,NpgsqlTransaction tx,Guid actor,string action,string type,string id,object? oldValue,object? newValue){using var q=new NpgsqlCommand("insert into internal_audit_log(actor_id,action,entity_type,entity_id,old_value,new_value) values($1,$2,$3,$4,$5::jsonb,$6::jsonb)",c,tx);q.Parameters.AddWithValue(actor);q.Parameters.AddWithValue(action);q.Parameters.AddWithValue(type);q.Parameters.AddWithValue(id);q.Parameters.AddWithValue(oldValue is null?(object)DBNull.Value:JsonSerializer.Serialize(oldValue));q.Parameters.AddWithValue(newValue is null?(object)DBNull.Value:JsonSerializer.Serialize(newValue));q.ExecuteNonQuery();}
}
#endif
