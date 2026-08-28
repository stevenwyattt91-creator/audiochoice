#if POSTGRES
using AudioChoice.Api.Contracts;
using Npgsql;

namespace AudioChoice.Api.Services;

public sealed class PostgresScanCatalog(NpgsqlDataSource dataSource) : IScanCatalog
{
    private const string FingerprintSelect = """
        e.fingerprint_version, e.sha256, e.file_size, e.duration_seconds,
        e.file_type, e.work_title, e.author, e.series_title, e.series_number,
        e.edition_type, e.part_number, e.total_parts
        """;

    public ScanResult? FindResult(BookFingerprint fingerprint)
    {
        using var connection = dataSource.OpenConnection();
        var editionID = FindEditionID(connection, null, fingerprint);
        return editionID is null ? null : FindLatestResult(connection, null, editionID.Value);
    }

    public ScanResult? FindExploreResult(string catalogID)
    {
        if (string.IsNullOrWhiteSpace(catalogID)) return null;
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            select id from audiobook_editions
            where explore_published = true and left(lower(sha256), 24) = $1
            limit 1;
            """, connection);
        command.Parameters.AddWithValue(catalogID.Trim().ToLowerInvariant());
        var editionID = command.ExecuteScalar() as Guid?;
        return editionID is null ? null : FindLatestResult(connection, null, editionID.Value);
    }

    public ScanJobRecord? FindActiveJob(BookFingerprint fingerprint)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand($"""
            select j.id, u.owner_user_id, j.upload_id, {FingerprintSelect}, j.status
            from scan_jobs j
            join scan_uploads u on u.id = j.upload_id
            join audiobook_editions e on e.id = j.edition_id
            where e.fingerprint_version = $1 and e.sha256 = $2 and e.file_size = $3
              and j.status in ('queued', 'processing')
            order by j.created_at limit 1;
            """, connection);
        AddFingerprintKey(command, fingerprint);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadJob(reader) : null;
    }

    public UploadRecord CreateUpload(
        Guid ownerUserID,
        CloudUploadAuthorizationRequest request,
        DateTimeOffset expiresAt,
        string token)
    {
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var editionID = UpsertEdition(connection, transaction, request.Fingerprint);
        var id = Guid.NewGuid();
        var objectName = $"temporary-audio/{id}.audio";
        using var command = new NpgsqlCommand("""
            insert into scan_uploads(
                id, edition_id, owner_user_id, object_name, expected_size,
                content_type, status, expires_at, delete_after, created_at, upload_token_hash)
            values ($1, $2, $3, $4, $5, $6, 'authorized', $7, $8, now(), $9);
            """, connection, transaction);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(editionID);
        command.Parameters.AddWithValue(ownerUserID);
        command.Parameters.AddWithValue(objectName);
        command.Parameters.AddWithValue(request.FileSize);
        command.Parameters.AddWithValue(request.ContentType);
        command.Parameters.AddWithValue(expiresAt);
        command.Parameters.AddWithValue(DateTimeOffset.UtcNow.AddHours(24));
        command.Parameters.AddWithValue(InMemoryScanCatalog.HashToken(token));
        command.ExecuteNonQuery();
        transaction.Commit();
        return new UploadRecord(
            id, ownerUserID, request.Fingerprint, Path.GetFileName(request.FileName),
            request.ContentType, request.FileSize, InMemoryScanCatalog.HashToken(token),
            expiresAt);
    }

    public UploadRecord? FindUpload(Guid uploadID)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand($"""
            select u.id, u.owner_user_id, {FingerprintSelect},
                   u.object_name, u.content_type, u.expected_size,
                   u.upload_token_hash, u.expires_at, u.status
            from scan_uploads u join audiobook_editions e on e.id = u.edition_id
            where u.id = $1;
            """, connection);
        command.Parameters.AddWithValue(uploadID);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var fingerprint = ReadFingerprint(reader, 2);
        var objectName = reader.GetString(14);
        var uploaded = reader.GetString(19) is "uploaded" or "processing" or "deleted";
        var deleted = reader.GetString(19) == "deleted";
        return new UploadRecord(
            reader.GetGuid(0), reader.GetGuid(1), fingerprint,
            Path.GetFileName(objectName), reader.GetString(15), reader.GetInt64(16),
            reader.GetString(17).Trim(), reader.GetFieldValue<DateTimeOffset>(18),
            uploaded && !deleted, uploaded && !deleted ? objectName : null, deleted);
    }

    public bool MarkUploaded(Guid uploadID, string storedPath)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            update scan_uploads set status = 'uploaded', object_name = $1
            where id = $2 and status = 'authorized' and expires_at > now();
            """, connection);
        command.Parameters.AddWithValue(storedPath);
        command.Parameters.AddWithValue(uploadID);
        return command.ExecuteNonQuery() > 0;
    }

    public ScanJobRecord? CreateJob(
        Guid ownerUserID, Guid uploadID, BookFingerprint fingerprint,
        string processingLane = ScanProcessingLanes.AzureOpenAI)
    {
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var uploadCommand = new NpgsqlCommand("""
            select edition_id from scan_uploads
            where id = $1 and owner_user_id = $2 and status = 'uploaded';
            """, connection, transaction);
        uploadCommand.Parameters.AddWithValue(uploadID);
        uploadCommand.Parameters.AddWithValue(ownerUserID);
        var editionValue = uploadCommand.ExecuteScalar();
        if (editionValue is not Guid editionID ||
            FindEditionID(connection, transaction, fingerprint) != editionID)
        {
            return null;
        }

        var existing = FindActiveJobForEdition(connection, transaction, editionID);
        if (existing is not null)
        {
            AddSubscriber(connection, transaction, existing.ID, ownerUserID);
            transaction.Commit();
            return existing with { OwnerUserID = ownerUserID };
        }
        var id = Guid.NewGuid();
        try
        {
            using var command = new NpgsqlCommand("""
                insert into scan_jobs(
                    id, edition_id, upload_id, processing_lane, status, attempt_count, available_at,
                    created_at, updated_at)
                values ($1, $2, $3, $4, 'queued', 0, now(), now(), now());
                """, connection, transaction);
            command.Parameters.AddWithValue(id);
            command.Parameters.AddWithValue(editionID);
            command.Parameters.AddWithValue(uploadID);
            command.Parameters.AddWithValue(processingLane);
            command.ExecuteNonQuery();
            AddSubscriber(connection, transaction, id, ownerUserID);
            transaction.Commit();
            return new ScanJobRecord(id, ownerUserID, uploadID, fingerprint, CloudScanStatus.Queued,
                ProcessingLane: processingLane);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            transaction.Rollback();
            return CreateJob(ownerUserID, uploadID, fingerprint, processingLane);
        }
    }

    public ScanJobRecord? CreateReanalysisJob(
        Guid ownerUserID, BookFingerprint fingerprint,
        string processingLane = ScanProcessingLanes.AzureOpenAI)
    {
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var editionID = FindEditionID(connection, transaction, fingerprint);
        if (editionID is null) return null;

        if (ownerUserID == Guid.Empty)
        {
            using var owner = new NpgsqlCommand("""
                select s.user_id
                from scan_jobs j
                join scan_job_subscribers s on s.scan_job_id = j.id
                where j.edition_id = $1
                order by j.updated_at desc limit 1;
                """, connection, transaction);
            owner.Parameters.AddWithValue(editionID.Value);
            ownerUserID = owner.ExecuteScalar() as Guid? ?? Guid.Empty;
            if (ownerUserID == Guid.Empty) return null;
        }

        var existing = FindActiveJobForEdition(connection, transaction, editionID.Value);
        if (existing is not null)
        {
            AddSubscriber(connection, transaction, existing.ID, ownerUserID);
            transaction.Commit();
            return existing with { OwnerUserID = ownerUserID };
        }

        Guid? uploadID;
        using (var source = new NpgsqlCommand("""
            select j.upload_id
            from scan_jobs j
            join scan_job_subscribers s on s.scan_job_id = j.id
            where j.edition_id = $1 and s.user_id = $2
            order by j.updated_at desc limit 1;
            """, connection, transaction))
        {
            source.Parameters.AddWithValue(editionID.Value);
            source.Parameters.AddWithValue(ownerUserID);
            uploadID = source.ExecuteScalar() as Guid?;
        }
        if (uploadID is null) return null;

        var id = Guid.NewGuid();
        try
        {
            using var command = new NpgsqlCommand("""
                insert into scan_jobs(
                    id, edition_id, upload_id, processing_lane, status, attempt_count, available_at,
                    progress_percent, progress_stage, created_at, updated_at)
                values ($1, $2, $3, $4, 'queued', 0, now(), 0, 'queued', now(), now());
                """, connection, transaction);
            command.Parameters.AddWithValue(id);
            command.Parameters.AddWithValue(editionID.Value);
            command.Parameters.AddWithValue(uploadID.Value);
            command.Parameters.AddWithValue(processingLane);
            command.ExecuteNonQuery();
            AddSubscriber(connection, transaction, id, ownerUserID);
            transaction.Commit();
            return new ScanJobRecord(
                id, ownerUserID, uploadID.Value, fingerprint, CloudScanStatus.Queued,
                ProcessingLane: processingLane);
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            transaction.Rollback();
            return CreateReanalysisJob(ownerUserID, fingerprint, processingLane);
        }
    }

    public ScanJobRecord? FindJob(Guid scanID)
    {
        using var connection = dataSource.OpenConnection();
        var job = FindJob(connection, null, scanID);
        if (job is null || job.Status != CloudScanStatus.Completed) return job;

        var editionID = FindEditionID(connection, null, job.Fingerprint);
        var result = editionID is null
            ? null
            : FindLatestResult(connection, null, editionID.Value);
        return job with { Result = result };
    }

    public bool CanAccessJob(Guid scanID, Guid userID)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            select exists(select 1 from scan_job_subscribers
                where scan_job_id = $1 and user_id = $2);
            """, connection);
        command.Parameters.AddWithValue(scanID);
        command.Parameters.AddWithValue(userID);
        return (bool)(command.ExecuteScalar() ?? false);
    }

    public bool SetJobStatus(Guid scanID, CloudScanStatus status)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            update scan_jobs set status = $1, updated_at = now(),
                attempt_count = case when $1 = 'processing' then attempt_count + 1 else attempt_count end
            where id = $2;
            """, connection);
        command.Parameters.AddWithValue(Status(status));
        command.Parameters.AddWithValue(scanID);
        return command.ExecuteNonQuery() > 0;
    }

    public ScanProgress GetJobProgress(Guid scanID)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(
            "select progress_percent, progress_stage, completed_chunks, total_chunks from scan_jobs where id = $1;", connection);
        command.Parameters.AddWithValue(scanID);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new ScanProgress(reader.GetInt32(0), NullableString(reader, 1), reader.GetInt32(2), reader.GetInt32(3))
            : new ScanProgress(0, null);
    }

    public bool UpdateJobProgress(Guid scanID, int percent, string stage)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            update scan_jobs set progress_percent = greatest(progress_percent, $1),
                progress_stage = case
                    when progress_stage = 'complete' then progress_stage
                    when progress_stage = 'finalizing' and $2 <> 'complete' then progress_stage
                    when $1 >= progress_percent then $2
                    else progress_stage
                end,
                updated_at = now() where id = $3;
            """, connection);
        command.Parameters.AddWithValue(Math.Clamp(percent, 0, 100));
        command.Parameters.AddWithValue(stage); command.Parameters.AddWithValue(scanID);
        return command.ExecuteNonQuery() > 0;
    }

    public bool UpdateChunkProgress(Guid scanID, int completedChunks, int totalChunks)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            update scan_jobs set completed_chunks = greatest(completed_chunks, $1),
                total_chunks = greatest(total_chunks, $2),
                updated_at = now() where id = $3;
            """, connection);
        command.Parameters.AddWithValue(Math.Max(0, completedChunks));
        command.Parameters.AddWithValue(Math.Max(0, totalChunks));
        command.Parameters.AddWithValue(scanID);
        return command.ExecuteNonQuery() > 0;
    }

    public bool CompleteJob(Guid scanID, ScanResult result)
    {
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var job = FindJob(connection, transaction, scanID);
        if (job is null) return false;
        SaveResult(connection, transaction, job.Fingerprint, result);
        using var command = new NpgsqlCommand("""
            update scan_jobs set status = 'completed', progress_percent = 100,
                progress_stage = 'complete', updated_at = now() where id = $1;
            """, connection, transaction);
        command.Parameters.AddWithValue(scanID);
        var updated = command.ExecuteNonQuery() > 0;
        transaction.Commit();
        return updated;
    }

    public bool FailJob(Guid scanID) => SetJobStatus(scanID, CloudScanStatus.Failed);

    public void SaveResult(BookFingerprint fingerprint, ScanResult result)
    {
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        SaveResult(connection, transaction, fingerprint, result);
        transaction.Commit();
    }

    public IReadOnlyList<ScanJobRecord> RecoverableJobs()
    {
        using var connection = dataSource.OpenConnection();
        using var reset = new NpgsqlCommand("""
            update scan_jobs set status = 'queued', lease_owner = null,
                lease_expires_at = null, updated_at = now()
            where status = 'processing'
              and (lease_expires_at is null or lease_expires_at <= now());
            """, connection);
        reset.ExecuteNonQuery();
        using var command = new NpgsqlCommand($"""
            select j.id, u.owner_user_id, j.upload_id, {FingerprintSelect}, j.status
            from scan_jobs j join scan_uploads u on u.id = j.upload_id
            join audiobook_editions e on e.id = j.edition_id
            where j.status = 'queued' order by j.available_at;
            """, connection);
        using var reader = command.ExecuteReader();
        var jobs = new List<ScanJobRecord>();
        while (reader.Read()) jobs.Add(ReadJob(reader));
        return jobs;
    }

    public IReadOnlyList<UploadRecord> ExpiredUploads(DateTimeOffset now)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand($"""
            select u.id, u.owner_user_id, {FingerprintSelect},
                   u.object_name, u.content_type, u.expected_size,
                   u.upload_token_hash, u.expires_at, u.status
            from scan_uploads u join audiobook_editions e on e.id = u.edition_id
            where (
                    u.status = 'authorized' and u.expires_at <= $1
                  ) or (
                    u.status in ('uploaded', 'failed') and u.delete_after <= $1
                    and not exists (
                        select 1 from scan_jobs j where j.upload_id = u.id
                        and j.status in ('queued', 'processing')
                    )
                  );
            """, connection);
        command.Parameters.AddWithValue(now);
        using var reader = command.ExecuteReader();
        var uploads = new List<UploadRecord>();
        while (reader.Read())
        {
            var fingerprint = ReadFingerprint(reader, 2);
            var objectName = reader.GetString(14);
            var uploaded = reader.GetString(19) != "authorized";
            uploads.Add(new UploadRecord(
                reader.GetGuid(0), reader.GetGuid(1), fingerprint,
                Path.GetFileName(objectName), reader.GetString(15), reader.GetInt64(16),
                reader.GetString(17).Trim(), reader.GetFieldValue<DateTimeOffset>(18),
                uploaded, uploaded ? objectName : null, false));
        }
        return uploads;
    }

    public bool MarkUploadDeleted(Guid uploadID)
    {
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var jobs = new NpgsqlCommand("""
            update scan_jobs set status = 'failed',
                last_error = 'Temporary audio expired before processing completed',
                updated_at = now()
            where upload_id = $1 and status in ('queued', 'processing');
            """, connection, transaction))
        {
            jobs.Parameters.AddWithValue(uploadID);
            jobs.ExecuteNonQuery();
        }
        using var command = new NpgsqlCommand("""
            update scan_uploads set status = 'deleted' where id = $1;
            """, connection, transaction);
        command.Parameters.AddWithValue(uploadID);
        var updated = command.ExecuteNonQuery() > 0;
        transaction.Commit();
        return updated;
    }

    public IReadOnlyList<ExploreCatalogBook> ListExploreBooks()
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand($"""
            select {FingerprintSelect}, r.scanned_at, r.scanner_version,
                   count(se.id)::int,
                   (count(se.id) filter (where nullif(se.aggregate_key, '') is null)
                    + count(distinct nullif(se.aggregate_key, '')))::int,
                   coalesce(array_agg(distinct se.group_id) filter (where se.group_id is not null), array[]::uuid[]),
                   e.cover_image is not null,
                   e.description
            from audiobook_editions e
            join lateral (
                select id, scanned_at, scanner_version from scan_results
                where edition_id = e.id order by scanned_at desc limit 1
            ) r on true
            left join scan_events se on se.scan_result_id = r.id
            where e.work_title is not null and btrim(e.work_title) <> ''
              and e.explore_published = true
              and lower(e.work_title) not like '%iron flame%'
            group by e.id, r.id, r.scanned_at, r.scanner_version
            order by e.work_title;
            """, connection);
        using var reader = command.ExecuteReader();
        var values = new List<ExploreCatalogBook>();
        while (reader.Read())
        {
            var fingerprint = ReadFingerprint(reader, 0);
            var result = new ScanResult(
                reader.GetFieldValue<Guid[]>(16).Select((groupID, index) => new ScanEvent(
                    Guid.Empty, index, index, Guid.Empty, groupID, Guid.Empty, 0)).ToArray(),
                reader.GetFieldValue<DateTimeOffset>(12),
                reader.GetString(13));
            var item = ExploreCatalog.Create(
                fingerprint,
                result,
                reader.GetBoolean(17),
                reader.IsDBNull(18) ? null : reader.GetString(18));
            values.Add(item with { EventCount = reader.GetInt32(15) });
        }
        return ExploreCatalog.Deduplicate(values);
    }

    public bool SaveEditionCover(BookFingerprint fingerprint, byte[] imageBytes, string contentType, bool replaceExisting = false)
    {
        if (imageBytes.Length == 0) return false;
        using var connection = dataSource.OpenConnection();
        var predicate = replaceExisting ? "" : " and cover_image is null";
        using var command = new NpgsqlCommand($"""
            update audiobook_editions
            set cover_image = $4, cover_image_content_type = $5
            where fingerprint_version = $1 and lower(sha256) = $2 and file_size = $3{predicate};
            """, connection);
        AddFingerprintKey(command, fingerprint);
        command.Parameters.AddWithValue(imageBytes);
        command.Parameters.AddWithValue(contentType);
        return command.ExecuteNonQuery() > 0;
    }

    public bool SaveEditionDescription(BookFingerprint fingerprint, string description)
    {
        var normalized = ExploreCatalog.NormalizeDescription(description);
        if (normalized is null) return false;
        using var connection = dataSource.OpenConnection();
        // `description is null` makes the first writer win: any listener who owns the
        // recording can report one, and a later import carrying a poorer tag must not
        // displace a synopsis that is already good.
        using var command = new NpgsqlCommand("""
            update audiobook_editions
            set description = $4
            where fingerprint_version = $1 and lower(sha256) = $2 and file_size = $3
              and description is null;
            """, connection);
        AddFingerprintKey(command, fingerprint);
        command.Parameters.AddWithValue(normalized);
        return command.ExecuteNonQuery() > 0;
    }

    public bool SaveExploreCover(string catalogID, byte[] imageBytes, string contentType, bool replaceExisting = false)
    {
        if (string.IsNullOrWhiteSpace(catalogID) || imageBytes.Length == 0) return false;
        using var connection = dataSource.OpenConnection();
        var sql = replaceExisting
            ? """
                update audiobook_editions
                set cover_image = $2, cover_image_content_type = $3
                where left(lower(sha256), 24) = $1;
                """
            : """
                update audiobook_editions
                set cover_image = $2, cover_image_content_type = $3
                where left(lower(sha256), 24) = $1 and cover_image is null;
                """;
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(catalogID.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue(imageBytes);
        command.Parameters.AddWithValue(contentType);
        return command.ExecuteNonQuery() > 0;
    }

    public (byte[] Bytes, string ContentType)? FindExploreCover(string catalogID)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            select cover_image, cover_image_content_type from audiobook_editions
            where left(lower(sha256), 24) = $1 and cover_image is not null limit 1;
            """, connection);
        command.Parameters.AddWithValue(catalogID.Trim().ToLowerInvariant());
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetFieldValue<byte[]>(0), reader.GetString(1)) : null;
    }

    public bool HideExploreBook(string catalogID)
    {
        if (string.IsNullOrWhiteSpace(catalogID)) return false;
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            update audiobook_editions set explore_published = false
            where left(lower(sha256), 24) = $1;
            """, connection);
        command.Parameters.AddWithValue(catalogID.Trim().ToLowerInvariant());
        return command.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<BookFingerprint> ListFingerprints()
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand($"""
            select {FingerprintSelect} from audiobook_editions e
            order by created_at;
            """, connection);
        using var reader = command.ExecuteReader();
        var fingerprints = new List<BookFingerprint>();
        while (reader.Read()) fingerprints.Add(ReadFingerprint(reader, 0));
        return fingerprints;
    }

    public bool UpdateEditionMetadata(AdminEditionMetadataRequest request)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            update audiobook_editions set
                work_title = $1,
                author = $2,
                series_title = $3,
                series_number = $4,
                edition_type = $5,
                part_number = $6,
                total_parts = $7,
                duration_seconds = coalesce($8, duration_seconds)
            where fingerprint_version = $9 and sha256 = $10 and file_size = $11
              and exists(select 1 from scan_results where edition_id = audiobook_editions.id);
            """, connection);
        command.Parameters.AddWithValue(request.WorkTitle.Trim());
        AddNullable(command, request.Author?.Trim());
        AddNullable(command, request.SeriesTitle?.Trim());
        AddNullable(command, request.SeriesNumber);
        AddNullable(command, request.EditionType?.Trim());
        AddNullable(command, request.PartNumber);
        AddNullable(command, request.TotalParts);
        AddNullable(command, request.Duration);
        command.Parameters.AddWithValue(request.Fingerprint.Version);
        command.Parameters.AddWithValue(request.Fingerprint.Sha256.ToLowerInvariant());
        command.Parameters.AddWithValue(request.Fingerprint.FileSize);
        return command.ExecuteNonQuery() == 1;
    }

    private static ScanJobRecord? FindActiveJobForEdition(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid editionID)
    {
        using var command = new NpgsqlCommand($"""
            select j.id, u.owner_user_id, j.upload_id, {FingerprintSelect}, j.status
            from scan_jobs j join scan_uploads u on u.id = j.upload_id
            join audiobook_editions e on e.id = j.edition_id
            where j.edition_id = $1 and j.status in ('queued', 'processing') limit 1;
            """, connection, transaction);
        command.Parameters.AddWithValue(editionID);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadJob(reader) : null;
    }

    private static void AddSubscriber(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid scanID,
        Guid userID)
    {
        using var command = new NpgsqlCommand("""
            insert into scan_job_subscribers(scan_job_id, user_id, created_at)
            values ($1, $2, now()) on conflict do nothing;
            """, connection, transaction);
        command.Parameters.AddWithValue(scanID);
        command.Parameters.AddWithValue(userID);
        command.ExecuteNonQuery();
    }

    private static ScanJobRecord? FindJob(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid scanID)
    {
        using var command = new NpgsqlCommand($"""
            select j.id, u.owner_user_id, j.upload_id, {FingerprintSelect}, j.status
            from scan_jobs j join scan_uploads u on u.id = j.upload_id
            join audiobook_editions e on e.id = j.edition_id where j.id = $1;
            """, connection, transaction);
        command.Parameters.AddWithValue(scanID);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadJob(reader) : null;
    }

    private static ScanJobRecord ReadJob(NpgsqlDataReader reader)
    {
        var fingerprint = ReadFingerprint(reader, 3);
        return new ScanJobRecord(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), fingerprint,
            ParseStatus(reader.GetString(15)));
    }

    private static void SaveResult(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        BookFingerprint fingerprint, ScanResult result)
    {
        var editionID = UpsertEdition(connection, transaction, fingerprint);
        var resultID = Guid.NewGuid();
        using (var command = new NpgsqlCommand("""
            insert into scan_results(id, edition_id, scanner_version, taxonomy_version, scanned_at)
            values ($1, $2, $3, '2.0', $4)
            on conflict (edition_id, scanner_version) do update set scanned_at = excluded.scanned_at
            returning id;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(resultID);
            command.Parameters.AddWithValue(editionID);
            command.Parameters.AddWithValue(result.ScannerVersion);
            command.Parameters.AddWithValue(result.ScanDate);
            resultID = (Guid)(command.ExecuteScalar() ?? resultID);
        }
        using (var delete = new NpgsqlCommand(
            "delete from scan_events where scan_result_id = $1;", connection, transaction))
        {
            delete.Parameters.AddWithValue(resultID);
            delete.ExecuteNonQuery();
        }
        foreach (var value in result.Events)
        {
            using var insert = new NpgsqlCommand("""
                insert into scan_events(
                    id, scan_result_id, start_seconds, end_seconds,
                    category_id, group_id, event_id, confidence, stable_key,
                    safe_description, aggregate_key, aggregate_display)
                values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12);
                """, connection, transaction);
            insert.Parameters.AddWithValue(value.Id);
            insert.Parameters.AddWithValue(resultID);
            insert.Parameters.AddWithValue(value.StartTime);
            insert.Parameters.AddWithValue(value.EndTime);
            insert.Parameters.AddWithValue(value.CategoryID);
            insert.Parameters.AddWithValue(value.GroupID);
            insert.Parameters.AddWithValue(value.EventID);
            insert.Parameters.AddWithValue(value.Confidence);
            insert.Parameters.AddWithValue(string.IsNullOrWhiteSpace(value.StableKey) ? new string('0', 64) : value.StableKey);
            insert.Parameters.AddWithValue(value.SafeDescription);
            AddNullable(insert, value.AggregateKey);
            AddNullable(insert, value.AggregateDisplay);
            insert.ExecuteNonQuery();
        }
    }

    private static ScanResult? FindLatestResult(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid editionID)
    {
        Guid resultID;
        DateTimeOffset scanDate;
        string scannerVersion;
        using (var command = new NpgsqlCommand("""
            select id, scanned_at, scanner_version from scan_results
            where edition_id = $1 order by scanned_at desc limit 1;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(editionID);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            resultID = reader.GetGuid(0);
            scanDate = reader.GetFieldValue<DateTimeOffset>(1);
            scannerVersion = reader.GetString(2);
        }
        var events = new List<ScanEvent>();
        using (var command = new NpgsqlCommand("""
            select id, start_seconds, end_seconds, category_id, group_id, event_id, confidence,
                   stable_key, safe_description, aggregate_key, aggregate_display
            from scan_events where scan_result_id = $1 order by start_seconds;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(resultID);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                events.Add(new ScanEvent(
                    reader.GetGuid(0), reader.GetDouble(1), reader.GetDouble(2),
                    reader.GetGuid(3), reader.GetGuid(4), reader.GetGuid(5), reader.GetDouble(6),
                    reader.GetString(7).Trim(), reader.GetString(8),
                    NullableString(reader, 9)?.Trim(), NullableString(reader, 10)));
            }
        }
        return new ScanResult(events, scanDate, scannerVersion);
    }

    private static Guid UpsertEdition(
        NpgsqlConnection connection, NpgsqlTransaction transaction, BookFingerprint value)
    {
        value = EditionTitleFormatter.Canonicalize(value);
        using var command = new NpgsqlCommand("""
            insert into audiobook_editions(
                id, fingerprint_version, sha256, file_size, duration_seconds, file_type,
                work_title, author, series_title, series_number, edition_type,
                part_number, total_parts, created_at)
            values ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,now())
            on conflict (fingerprint_version, sha256, file_size) do update set
                duration_seconds = coalesce(excluded.duration_seconds, audiobook_editions.duration_seconds)
            returning id;
            """, connection, transaction);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(value.Version);
        command.Parameters.AddWithValue(value.Sha256.ToLowerInvariant());
        command.Parameters.AddWithValue(value.FileSize);
        AddNullable(command, value.Duration);
        command.Parameters.AddWithValue(value.FileType);
        AddNullable(command, EditionTitleFormatter.Format(value));
        AddNullable(command, value.Author);
        AddNullable(command, value.SeriesTitle);
        AddNullable(command, value.SeriesNumber);
        AddNullable(command, value.EditionType);
        AddNullable(command, value.PartNumber);
        AddNullable(command, value.TotalParts);
        return (Guid)(command.ExecuteScalar() ?? throw new InvalidOperationException());
    }

    private static Guid? FindEditionID(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, BookFingerprint value)
    {
        using var command = new NpgsqlCommand("""
            select id from audiobook_editions
            where fingerprint_version = $1 and sha256 = $2 and file_size = $3;
            """, connection, transaction);
        AddFingerprintKey(command, value);
        return command.ExecuteScalar() as Guid?;
    }

    private static BookFingerprint ReadFingerprint(NpgsqlDataReader reader, int start) => new(
        reader.GetInt32(start), reader.GetString(start + 1).Trim(), reader.GetInt64(start + 2),
        Nullable<double>(reader, start + 3), reader.GetString(start + 4),
        NullableString(reader, start + 5), NullableString(reader, start + 6),
        NullableString(reader, start + 7), Nullable<int>(reader, start + 8),
        NullableString(reader, start + 9), Nullable<int>(reader, start + 10),
        Nullable<int>(reader, start + 11));

    private static void AddFingerprintKey(NpgsqlCommand command, BookFingerprint value)
    {
        command.Parameters.AddWithValue(value.Version);
        command.Parameters.AddWithValue(value.Sha256.ToLowerInvariant());
        command.Parameters.AddWithValue(value.FileSize);
    }
    private static void AddNullable(NpgsqlCommand command, object? value) =>
        command.Parameters.AddWithValue(value ?? DBNull.Value);
    private static T? Nullable<T>(NpgsqlDataReader reader, int ordinal) where T : struct =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);
    private static string? NullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static string Status(CloudScanStatus value) => value switch
    {
        CloudScanStatus.Queued => "queued",
        CloudScanStatus.Processing => "processing",
        CloudScanStatus.Completed => "completed",
        CloudScanStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
    private static CloudScanStatus ParseStatus(string value) => value switch
    {
        "queued" => CloudScanStatus.Queued,
        "processing" => CloudScanStatus.Processing,
        "completed" => CloudScanStatus.Completed,
        "failed" => CloudScanStatus.Failed,
        _ => throw new InvalidOperationException($"Unknown scan status: {value}")
    };
}
#endif
