using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

public sealed record UploadRecord(
    Guid ID,
    Guid OwnerUserID,
    BookFingerprint Fingerprint,
    string FileName,
    string ContentType,
    long FileSize,
    string TokenHash,
    DateTimeOffset ExpiresAt,
    bool IsUploaded = false,
    string? StoredPath = null,
    bool IsDeleted = false);

public sealed record ScanJobRecord(
    Guid ID,
    Guid OwnerUserID,
    Guid UploadID,
    BookFingerprint Fingerprint,
    CloudScanStatus Status,
    ScanResult? Result = null,
    string ProcessingLane = ScanProcessingLanes.AzureOpenAI);

public interface IScanCatalog
{
    ScanResult? FindResult(BookFingerprint fingerprint);
    /// <summary>Returns the verified result published for a specific Explore edition.</summary>
    ScanResult? FindExploreResult(string catalogID);
    ScanJobRecord? FindActiveJob(BookFingerprint fingerprint);
    UploadRecord CreateUpload(
        Guid ownerUserID,
        CloudUploadAuthorizationRequest request,
        DateTimeOffset expiresAt,
        string token);
    UploadRecord? FindUpload(Guid uploadID);
    bool MarkUploaded(Guid uploadID, string storedPath);
    ScanJobRecord? CreateJob(
        Guid ownerUserID, Guid uploadID, BookFingerprint fingerprint,
        string processingLane = ScanProcessingLanes.AzureOpenAI);
    ScanJobRecord? CreateReanalysisJob(
        Guid ownerUserID, BookFingerprint fingerprint,
        string processingLane = ScanProcessingLanes.AzureOpenAI);
    ScanJobRecord? FindJob(Guid scanID);
    bool CanAccessJob(Guid scanID, Guid userID);
    bool SetJobStatus(Guid scanID, CloudScanStatus status);
    ScanProgress GetJobProgress(Guid scanID);
    bool UpdateJobProgress(Guid scanID, int percent, string stage);
    bool UpdateChunkProgress(Guid scanID, int completedChunks, int totalChunks);
    bool CompleteJob(Guid scanID, ScanResult result);
    bool FailJob(Guid scanID);
    void SaveResult(
        BookFingerprint fingerprint,
        ScanResult result);
    IReadOnlyList<ScanJobRecord> RecoverableJobs();
    IReadOnlyList<UploadRecord> ExpiredUploads(DateTimeOffset now);
    bool MarkUploadDeleted(Guid uploadID);
    IReadOnlyList<ExploreCatalogBook> ListExploreBooks();
    bool SaveExploreCover(string catalogID, byte[] imageBytes, string contentType, bool replaceExisting = false);
    bool SaveEditionCover(BookFingerprint fingerprint, byte[] imageBytes, string contentType, bool replaceExisting = false);
    /// <summary>
    /// Stores the synopsis a client read out of the file's own description tags.
    /// </summary>
    /// <remarks>
    /// First writer wins. Any listener who owns the recording can supply this, so a
    /// later import with a worse tag must not overwrite a good synopsis already held.
    /// </remarks>
    bool SaveEditionDescription(BookFingerprint fingerprint, string description);
    (byte[] Bytes, string ContentType)? FindExploreCover(string catalogID);
    bool HideExploreBook(string catalogID);
    /// <summary>
    /// Puts a hidden edition back in the catalogue.
    /// </summary>
    /// <remarks>
    /// Hiding was a one-way door, so a mistake could only be undone by editing the database
    /// by hand.
    /// </remarks>
    bool RestoreExploreBook(string catalogID);
    /// <summary>
    /// Every scanned edition with its catalogue status, for administration.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="ListExploreBooks"/> this withholds nothing and does not merge
    /// duplicates, because the entries an administrator needs to act on are precisely the
    /// ones the listener-facing view removes.
    /// </remarks>
    IReadOnlyList<ExploreCatalogAdminEntry> ListExploreCatalog();
    IReadOnlyList<BookFingerprint> ListFingerprints();
    bool UpdateEditionMetadata(AdminEditionMetadataRequest request);
}

public sealed record ScanProgress(
    int Percent,
    string? Stage,
    int CompletedChunks = 0,
    int TotalChunks = 0);

public sealed class InMemoryScanCatalog : IScanCatalog
{
    private readonly Dictionary<string, (byte[] Bytes, string ContentType)> _exploreCovers = new();
    private readonly ConcurrentDictionary<string, ScanResult> _results = new();
    private readonly ConcurrentDictionary<Guid, UploadRecord> _uploads = new();
    private readonly ConcurrentDictionary<Guid, ScanJobRecord> _jobs = new();
    private readonly ConcurrentDictionary<Guid, ScanProgress> _progress = new();
    private readonly ConcurrentDictionary<string, byte> _hiddenExploreBooks = new();
    /// Keyed by catalog ID, matching how covers are keyed.
    private readonly ConcurrentDictionary<string, string> _editionDescriptions = new();
    private readonly ConcurrentDictionary<Guid, HashSet<Guid>> _jobSubscribers = new();
    private readonly object _subscriberLock = new();
    private readonly string? _storagePath;
    private readonly object _persistenceLock = new();

    /// <param name="editionSignatures">
    /// Supplies the retail product identifier for an edition, so an Explore entry can link
    /// to an exact Audible listing. Optional: without it entries fall back to a search,
    /// which is what tests and the no-database mode get.
    /// </param>
    public InMemoryScanCatalog(
        string? storagePath = null,
        IEditionSignatureStore? editionSignatures = null)
    {
        _storagePath = storagePath;
        _editionSignatures = editionSignatures;
        Load();
    }

    private readonly IEditionSignatureStore? _editionSignatures;

    public ScanResult? FindResult(BookFingerprint fingerprint) =>
        _results.GetValueOrDefault(FingerprintKey(fingerprint));

    public ScanResult? FindExploreResult(string catalogID)
    {
        if (string.IsNullOrWhiteSpace(catalogID)) return null;
        var prefix = catalogID.Trim().ToLowerInvariant();
        return _results.FirstOrDefault(pair =>
            pair.Key.Split(':').ElementAtOrDefault(1)?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true).Value;
    }

    public ScanJobRecord? FindActiveJob(BookFingerprint fingerprint)
    {
        var key = FingerprintKey(fingerprint);

        return _jobs.Values.FirstOrDefault(job =>
            FingerprintKey(job.Fingerprint) == key &&
            job.Status is CloudScanStatus.Queued or CloudScanStatus.Processing);
    }

    public UploadRecord CreateUpload(
        Guid ownerUserID,
        CloudUploadAuthorizationRequest request,
        DateTimeOffset expiresAt,
        string token)
    {
        var upload = new UploadRecord(
            Guid.NewGuid(),
            ownerUserID,
            request.Fingerprint,
            Path.GetFileName(request.FileName),
            request.ContentType,
            request.FileSize,
            HashToken(token),
            expiresAt);

        _uploads[upload.ID] = upload;
        Persist();
        return upload;
    }

    public UploadRecord? FindUpload(Guid uploadID) =>
        _uploads.GetValueOrDefault(uploadID);

    public bool MarkUploaded(Guid uploadID, string storedPath)
    {
        if (!_uploads.TryGetValue(uploadID, out var upload))
        {
            return false;
        }

        var updated = _uploads.TryUpdate(
            uploadID,
            upload with { IsUploaded = true, StoredPath = storedPath },
            upload);
        if (updated) Persist();
        return updated;
    }

    public ScanJobRecord? CreateJob(
        Guid ownerUserID,
        Guid uploadID,
        BookFingerprint fingerprint,
        string processingLane = ScanProcessingLanes.AzureOpenAI)
    {
        var upload = FindUpload(uploadID);

        if (upload is null ||
            upload.OwnerUserID != ownerUserID ||
            !upload.IsUploaded ||
            FingerprintKey(upload.Fingerprint) != FingerprintKey(fingerprint))
        {
            return null;
        }

        var existing = FindActiveJob(fingerprint);
        if (existing is not null)
        {
            AddSubscriber(existing.ID, ownerUserID);
            return existing with { OwnerUserID = ownerUserID };
        }

        var job = new ScanJobRecord(
            Guid.NewGuid(),
            ownerUserID,
            uploadID,
            fingerprint,
            CloudScanStatus.Queued,
            ProcessingLane: processingLane);

        _jobs[job.ID] = job;
        AddSubscriber(job.ID, ownerUserID);
        Persist();
        return job;
    }

    public ScanJobRecord? CreateReanalysisJob(
        Guid ownerUserID,
        BookFingerprint fingerprint,
        string processingLane = ScanProcessingLanes.AzureOpenAI)
    {
        var key = FingerprintKey(fingerprint);
        if (ownerUserID == Guid.Empty)
        {
            ownerUserID = _jobs.Values
                .Where(job => FingerprintKey(job.Fingerprint) == key)
                .OrderByDescending(job => job.ID)
                .Select(job => job.OwnerUserID)
                .FirstOrDefault();
            if (ownerUserID == Guid.Empty) return null;
        }

        var existing = FindActiveJob(fingerprint);
        if (existing is not null)
        {
            AddSubscriber(existing.ID, ownerUserID);
            return existing with { OwnerUserID = ownerUserID };
        }

        var source = _jobs.Values
            .Where(job => FingerprintKey(job.Fingerprint) == key &&
                CanAccessJob(job.ID, ownerUserID))
            .OrderByDescending(job => job.ID)
            .FirstOrDefault();
        if (source is null) return null;

        var job = new ScanJobRecord(
            Guid.NewGuid(), ownerUserID, source.UploadID,
            fingerprint, CloudScanStatus.Queued, ProcessingLane: processingLane);
        _jobs[job.ID] = job;
        AddSubscriber(job.ID, ownerUserID);
        Persist();
        return job;
    }

    public ScanJobRecord? FindJob(Guid scanID) =>
        _jobs.GetValueOrDefault(scanID);

    public bool CanAccessJob(Guid scanID, Guid userID)
    {
        lock (_subscriberLock)
        {
            return (_jobSubscribers.TryGetValue(scanID, out var users) &&
                users.Contains(userID)) ||
                (_jobs.TryGetValue(scanID, out var job) && job.OwnerUserID == userID);
        }
    }

    public bool SetJobStatus(Guid scanID, CloudScanStatus status)
    {
        if (!_jobs.TryGetValue(scanID, out var job))
        {
            return false;
        }

        var updated = _jobs.TryUpdate(
            scanID,
            job with { Status = status },
            job);
        if (updated) Persist();
        return updated;
    }

    public ScanProgress GetJobProgress(Guid scanID) =>
        _progress.GetValueOrDefault(scanID) ?? new ScanProgress(0, null);

    public bool UpdateJobProgress(Guid scanID, int percent, string stage)
    {
        if (!_jobs.ContainsKey(scanID)) return false;
        var nextPercent = Math.Clamp(percent, 0, 100);
        var current = GetJobProgress(scanID);
        var nextStage = current.Stage is "complete" ||
            (current.Stage is "finalizing" && stage != "complete") ||
            nextPercent < current.Percent
            ? current.Stage
            : stage;
        _progress[scanID] = new ScanProgress(Math.Max(current.Percent, nextPercent), nextStage);
        return true;
    }

    public bool UpdateChunkProgress(Guid scanID, int completedChunks, int totalChunks)
    {
        if (!_jobs.ContainsKey(scanID)) return false;
        var current = GetJobProgress(scanID);
        var completed = Math.Max(current.CompletedChunks, Math.Max(0, completedChunks));
        var total = Math.Max(current.TotalChunks, Math.Max(0, totalChunks));
        var percent = total == 0 ? current.Percent : Math.Max(current.Percent, completed * 100 / total);
        _progress[scanID] = new ScanProgress(percent, "transcribing", completed, total);
        return true;
    }

    public bool CompleteJob(Guid scanID, ScanResult result)
    {
        if (!_jobs.TryGetValue(scanID, out var job))
        {
            return false;
        }

        var completed = job with
        {
            Status = CloudScanStatus.Completed,
            Result = result
        };
        _progress[scanID] = new ScanProgress(100, "complete");

        if (!_jobs.TryUpdate(scanID, completed, job))
        {
            return false;
        }

        SaveResult(job.Fingerprint, result);
        Persist();
        return true;
    }

    public bool FailJob(Guid scanID) =>
        SetJobStatus(scanID, CloudScanStatus.Failed);

    public void SaveResult(
        BookFingerprint fingerprint,
        ScanResult result)
    {
        _results[FingerprintKey(fingerprint)] = result;
        Persist();
    }

    public IReadOnlyList<ScanJobRecord> RecoverableJobs() =>
        _jobs.Values.Where(job =>
            job.Status is CloudScanStatus.Queued or CloudScanStatus.Processing)
        .ToArray();

    public IReadOnlyList<UploadRecord> ExpiredUploads(DateTimeOffset now) =>
        _uploads.Values.Where(value =>
            !value.IsDeleted &&
            value.ExpiresAt <= now &&
            (!value.IsUploaded || !_jobs.Values.Any(job =>
                job.UploadID == value.ID &&
                job.Status is CloudScanStatus.Queued or CloudScanStatus.Processing)))
        .ToArray();

    public bool MarkUploadDeleted(Guid uploadID)
    {
        if (!_uploads.TryGetValue(uploadID, out var upload)) return false;
        var updated = _uploads.TryUpdate(
            uploadID,
            upload with { IsUploaded = false, StoredPath = null, IsDeleted = true },
            upload);
        if (updated) Persist();
        return updated;
    }

    public IReadOnlyList<ExploreCatalogBook> ListExploreBooks() =>
        ExploreCatalog.Deduplicate(CompletedExploreBooks());

    private IEnumerable<ExploreCatalogBook> CompletedExploreBooks() => _jobs.Values
        .Where(value => value.Status == CloudScanStatus.Completed && value.Result is not null)
        .GroupBy(value => FingerprintKey(value.Fingerprint))
        .Select(group => group.OrderByDescending(value => value.Result!.ScanDate).First())
        .Where(value => !string.IsNullOrWhiteSpace(value.Fingerprint.WorkTitle))
        .Where(value => !value.Fingerprint.WorkTitle!.Contains("Iron Flame", StringComparison.OrdinalIgnoreCase))
        // A catalogue entry has to name a book. Anything still called "Imported audiobook"
        // was never identified and is not something another listener can look up or buy.
        .Where(value => ExploreCatalog.IsPublishable(value.Fingerprint))
        .Select(value => ExploreCatalog.Create(
            value.Fingerprint,
            value.Result!,
            _exploreCovers.ContainsKey(CatalogIDOf(value.Fingerprint)),
            _editionDescriptions.TryGetValue(CatalogIDOf(value.Fingerprint), out var description)
                ? description
                : null,
            _editionSignatures?.Find(value.Fingerprint)?.ProductIdentifier))
        .Where(value => !_hiddenExploreBooks.ContainsKey(value.CatalogID))
        .OrderBy(value => value.Title);

    private static string CatalogIDOf(BookFingerprint fingerprint) =>
        fingerprint.Sha256[..Math.Min(24, fingerprint.Sha256.Length)].ToLowerInvariant();

    public bool SaveExploreCover(string catalogID, byte[] imageBytes, string contentType, bool replaceExisting = false)
    {
        if (string.IsNullOrWhiteSpace(catalogID) || imageBytes.Length == 0) return false;
        _exploreCovers[catalogID.Trim().ToLowerInvariant()] = (imageBytes, contentType);
        return true;
    }

    public bool SaveEditionCover(BookFingerprint fingerprint, byte[] imageBytes, string contentType, bool replaceExisting = false) =>
        SaveExploreCover(fingerprint.Sha256[..Math.Min(24, fingerprint.Sha256.Length)], imageBytes, contentType, replaceExisting);

    public bool SaveEditionDescription(BookFingerprint fingerprint, string description)
    {
        var normalized = ExploreCatalog.NormalizeDescription(description);
        if (normalized is null) return false;
        // First writer wins, so a later import carrying a poorer tag cannot displace a
        // synopsis already stored for this edition.
        return _editionDescriptions.TryAdd(CatalogIDOf(fingerprint), normalized);
    }

    public (byte[] Bytes, string ContentType)? FindExploreCover(string catalogID) =>
        _exploreCovers.TryGetValue(catalogID.Trim().ToLowerInvariant(), out var cover) ? cover : null;

    public bool HideExploreBook(string catalogID)
    {
        if (string.IsNullOrWhiteSpace(catalogID)) return false;
        _hiddenExploreBooks[catalogID.Trim().ToLowerInvariant()] = 0;
        return true;
    }

    public bool RestoreExploreBook(string catalogID) =>
        !string.IsNullOrWhiteSpace(catalogID) &&
        _hiddenExploreBooks.TryRemove(catalogID.Trim().ToLowerInvariant(), out _);

    public IReadOnlyList<ExploreCatalogAdminEntry> ListExploreCatalog() => _jobs.Values
        .Where(value => value.Status == CloudScanStatus.Completed && value.Result is not null)
        .GroupBy(value => FingerprintKey(value.Fingerprint))
        .Select(group => group.OrderByDescending(value => value.Result!.ScanDate).First())
        .Select(value => ExploreCatalog.AdminEntry(
            ExploreCatalog.Create(
                value.Fingerprint,
                value.Result!,
                _exploreCovers.ContainsKey(CatalogIDOf(value.Fingerprint)),
                _editionDescriptions.TryGetValue(CatalogIDOf(value.Fingerprint), out var description)
                    ? description
                    : null,
                _editionSignatures?.Find(value.Fingerprint)?.ProductIdentifier),
            value.Fingerprint,
            !_hiddenExploreBooks.ContainsKey(CatalogIDOf(value.Fingerprint))))
        .OrderBy(value => value.Book.Title)
        .ToArray();

    public IReadOnlyList<BookFingerprint> ListFingerprints() =>
        _uploads.Values
            .Select(value => value.Fingerprint)
            .Concat(_jobs.Values.Select(value => value.Fingerprint))
            .GroupBy(FingerprintKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

    public bool UpdateEditionMetadata(AdminEditionMetadataRequest request)
    {
        if (!_results.ContainsKey(FingerprintKey(request.Fingerprint))) return false;
        var description = ExploreCatalog.NormalizeDescription(request.Description);
        // Overwrites, unlike a client report: an administrator correcting an entry is a
        // deliberate act, where a client is one of many owners reporting the same file.
        if (description is not null)
        {
            _editionDescriptions[CatalogIDOf(request.Fingerprint)] = description;
        }
        return true;
    }

    public static string FingerprintKey(BookFingerprint fingerprint) =>
        $"{fingerprint.Version}:{fingerprint.Sha256.ToLowerInvariant()}:{fingerprint.FileSize}";

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private void AddSubscriber(Guid scanID, Guid userID)
    {
        lock (_subscriberLock)
        {
            var users = _jobSubscribers.GetOrAdd(scanID, _ => []);
            users.Add(userID);
        }
    }

    private void Load()
    {
        if (string.IsNullOrWhiteSpace(_storagePath) || !File.Exists(_storagePath)) return;
        try
        {
            var state = JsonSerializer.Deserialize<CatalogState>(File.ReadAllText(_storagePath));
            if (state is null) return;
            foreach (var item in state.Results) _results[item.Key] = item.Value;
            foreach (var item in state.Uploads) _uploads[item.ID] = item;
            foreach (var item in state.Jobs) _jobs[item.ID] = item;
        }
        catch (JsonException)
        {
            File.Move(_storagePath, _storagePath + $".corrupt-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
        }
    }

    private void Persist()
    {
        if (string.IsNullOrWhiteSpace(_storagePath)) return;
        lock (_persistenceLock)
        {
            var directory = Path.GetDirectoryName(_storagePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporary = _storagePath + ".tmp";
            var state = new CatalogState(
                new Dictionary<string, ScanResult>(_results),
                _uploads.Values.ToArray(),
                _jobs.Values.ToArray());
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, _storagePath, true);
        }
    }

    private sealed record CatalogState(
        IReadOnlyDictionary<string, ScanResult> Results,
        IReadOnlyList<UploadRecord> Uploads,
        IReadOnlyList<ScanJobRecord> Jobs);
}

public static class ExploreCatalog
{
    /// <summary>
    /// Collapses entries that describe the same recording.
    /// </summary>
    /// <remarks>
    /// The same audiobook arrives from different listeners' files and their tags disagree:
    /// one carries an author and another does not, one title picks up "(Unabridged)", a
    /// bracketed edition note or a series prefix. Grouping on title-plus-author alone left
    /// a second row for a book already listed.
    ///
    /// Three things are deliberately kept apart. Parts of a split release are different
    /// audio. So are different edition types, where a dramatised recording and a straight
    /// reading have their own runtimes and their own scans. And two different authors
    /// sharing a title are two different books. Merging any of those would show one
    /// recording's filter data against another's audio.
    ///
    /// Mirrors ExploreCatalogCleanup on the clients. They keep their own pass so an older
    /// app still tidies a catalogue from a server that has not deployed this yet, and the
    /// rules match so the two cannot pick different survivors.
    /// </remarks>
    public static IReadOnlyList<ExploreCatalogBook> Deduplicate(
        IEnumerable<ExploreCatalogBook> books)
    {
        var clusters = new List<List<ExploreCatalogBook>>();
        // A product identifier names one published recording outright, so two copies sharing
        // one are the same catalogue entry however their titles are spelled. Consulted first
        // because it is the only signal here that cannot be mistaken.
        var byIdentifier = new Dictionary<string, int>(StringComparer.Ordinal);
        var byKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        foreach (var book in books)
        {
            var identifier = book.ProductIdentifier;
            if (identifier is not null && byIdentifier.TryGetValue(identifier, out var known))
            {
                clusters[known].Add(book);
                continue;
            }

            var key = GroupKey(book);
            if (!byKey.TryGetValue(key, out var indexes))
            {
                indexes = [];
                byKey[key] = indexes;
            }

            var placed = -1;
            foreach (var index in indexes)
            {
                if (!IsSameBook(clusters[index][0], book)) continue;
                clusters[index].Add(book);
                placed = index;
                break;
            }

            // Same author, same part, same runtime to the second: one recording whose title
            // is written two ways. Only reached when the titles disagree, since a matching
            // title would have been caught above.
            if (placed < 0)
            {
                for (var index = 0; index < clusters.Count; index++)
                {
                    if (!IsSameRecording(clusters[index][0], book)) continue;
                    clusters[index].Add(book);
                    placed = index;
                    break;
                }
            }

            if (placed < 0)
            {
                clusters.Add([book]);
                placed = clusters.Count - 1;
                indexes.Add(placed);
            }

            // Remember the identifier against whichever cluster this joined, so a later copy
            // carrying the same one lands in the same place.
            if (identifier is not null) byIdentifier.TryAdd(identifier, placed);
        }

        // Incoming order is preserved, so the caller's sort still holds.
        return clusters.Select(Best).ToArray();
    }

    /// <summary>
    /// Whether two entries are the same recording judged on runtime rather than title.
    /// </summary>
    /// <remarks>
    /// The last resort for copies that carry no product identifier, where one was tagged by
    /// hand and the other named from a filename. Runtime is the discriminating signal: two
    /// different recordings of the same book run to different lengths, because a different
    /// narrator reads at a different pace.
    ///
    /// Requires an author on both sides. Without one this would merge on runtime alone, and
    /// two unrelated books that happen to run the same length are not the same book.
    /// </remarks>
    private static bool IsSameRecording(ExploreCatalogBook left, ExploreCatalogBook right)
    {
        // Two identifiers that disagree are evidence of two different recordings, and that
        // outranks a runtime coincidence. Equal identifiers never reach here, having already
        // merged above, so anything left carrying one on both sides is genuinely two books.
        if (left.ProductIdentifier is not null && right.ProductIdentifier is not null) return false;
        if (left.Duration is not > 0 || right.Duration is not > 0) return false;
        if (Math.Abs(left.Duration.Value - right.Duration.Value) > RuntimeMatchSeconds) return false;
        if (PartMarker(left.Title) != PartMarker(right.Title)) return false;
        if (Normalize(left.EditionType) != Normalize(right.EditionType)) return false;
        var leftAuthor = Normalize(left.Author);
        var rightAuthor = Normalize(right.Author);
        return leftAuthor.Length > 0 && leftAuthor == rightAuthor;
    }

    /// <summary>
    /// Whether two runtimes are close enough to be the same recording.
    /// </summary>
    /// <remarks>
    /// An unknown runtime counts as agreement rather than a mismatch, because an untagged
    /// file is one of the differences being reconciled here.
    /// </remarks>
    private static bool RuntimesAgree(double? left, double? right) =>
        left is not > 0 || right is not > 0 ||
        Math.Abs(left.Value - right.Value) <= RuntimeMatchSeconds;

    /// <summary>
    /// How far two runtimes may differ and still be the same recording.
    /// </summary>
    /// <remarks>
    /// Converting a file re-encodes it, which shifts the reported length by a fraction of a
    /// second, and a container's runtime is rounded. Two seconds absorbs that without being
    /// wide enough to merge an abridged reading with an unabridged one.
    /// </remarks>
    private const double RuntimeMatchSeconds = 2;

    /// <summary>
    /// Which entry survives: a cover first, since a missing one is the visible symptom,
    /// then the richest scan, the most recent, and the fullest metadata.
    /// </summary>
    private static ExploreCatalogBook Best(List<ExploreCatalogBook> candidates) =>
        candidates.Skip(1).Aggregate(candidates[0],
            (current, candidate) => IsBetter(candidate, current) ? candidate : current);

    private static bool IsBetter(ExploreCatalogBook candidate, ExploreCatalogBook current)
    {
        var candidateHasCover = !string.IsNullOrWhiteSpace(candidate.CoverImageURL);
        if (candidateHasCover != !string.IsNullOrWhiteSpace(current.CoverImageURL))
        {
            return candidateHasCover;
        }
        if (candidate.EventCount != current.EventCount) return candidate.EventCount > current.EventCount;
        if (candidate.ScanDate != current.ScanDate) return candidate.ScanDate > current.ScanDate;
        var candidateScore = MetadataScore(candidate);
        var currentScore = MetadataScore(current);
        if (candidateScore != currentScore) return candidateScore > currentScore;
        // Deterministic last resort, so repeated calls cannot swap which of two equals wins.
        return string.CompareOrdinal(candidate.CatalogID, current.CatalogID) < 0;
    }

    private static int MetadataScore(ExploreCatalogBook book)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(book.Author)) score++;
        if (!string.IsNullOrWhiteSpace(book.Description)) score++;
        if (book.Duration is > 0) score++;
        if (!string.IsNullOrWhiteSpace(book.SeriesTitle)) score++;
        return score;
    }

    /// <summary>
    /// Cheap bucket key. Excludes the author on purpose, because a missing author is one of
    /// the differences being reconciled; <see cref="IsSameBook"/> decides real matches.
    /// </summary>
    private static string GroupKey(ExploreCatalogBook book) =>
        $"{NormalizedTitle(book.Title)}|{PartMarker(book.Title)}|{Normalize(book.EditionType)}";

    private static bool IsSameBook(ExploreCatalogBook left, ExploreCatalogBook right)
    {
        if (NormalizedTitle(left.Title) != NormalizedTitle(right.Title)) return false;
        if (PartMarker(left.Title) != PartMarker(right.Title)) return false;
        if (Normalize(left.EditionType) != Normalize(right.EditionType)) return false;
        // Two runtimes that disagree are two different readings of one book, whatever the
        // edition type says: a different narrator, or an abridgement. Merging them would show
        // one entry's scan for audio it does not describe.
        if (!RuntimesAgree(left.Duration, right.Duration)) return false;

        var leftAuthor = Normalize(left.Author);
        var rightAuthor = Normalize(right.Author);
        // An absent author means an untagged file, not a different recording. Two authors
        // that genuinely differ keep their own entries: a shared title is not evidence of
        // a shared book.
        return leftAuthor.Length == 0 || rightAuthor.Length == 0 || leftAuthor == rightAuthor;
    }

    /// <summary>"2 of 3", however it was written, or an empty string.</summary>
    private static string PartMarker(string title)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            title,
            @"(?:part|disc|volume|vol|book)?\s*(\d+)\s*(?:of|/)\s*(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? $"{match.Groups[1].Value} of {match.Groups[2].Value}" : "";
    }

    private static readonly string[] EditionSuffixes =
    [
        "unabridged", "abridged", "audiobook", "audio book", "audio edition",
        "a full cast production", "full cast production", "dramatized adaptation",
        "dramatised adaptation", "graphic audio", "graphicaudio"
    ];

    private static string NormalizedTitle(string title)
    {
        var value = title;
        foreach (var pattern in new[] { @"\([^)]*\)", @"\[[^\]]*\]", @"\{[^}]*\}" })
        {
            value = System.Text.RegularExpressions.Regex.Replace(value, pattern, " ");
        }
        value = System.Text.RegularExpressions.Regex.Replace(
            value,
            @"(?:part|disc|volume|vol|book)?\s*\d+\s*(?:of|/)\s*\d+",
            " ",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Only trailing edition wording is removed, so a title that genuinely contains one
        // of these words keeps it.
        var trimmed = true;
        while (trimmed)
        {
            trimmed = false;
            var simplified = Normalize(value);
            foreach (var suffix in EditionSuffixes)
            {
                if (!simplified.EndsWith(" " + suffix, StringComparison.Ordinal)) continue;
                value = simplified[..^(suffix.Length + 1)];
                trimmed = true;
                break;
            }
        }
        return Normalize(value);
    }

    /// <summary>Kept for callers that only need a bucket, not the full clustering.</summary>
    public static string CanonicalKey(ExploreCatalogBook book) =>
        $"{NormalizedTitle(book.Title)}|{Normalize(book.Author)}|{PartMarker(book.Title)}";

    private static string Normalize(string? value) =>
        System.Text.RegularExpressions.Regex.Replace(value ?? "", @"[^a-z0-9]+", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim().ToLowerInvariant();

    /// Every Explore entry now points at Audible.
    public const string PurchaseProviderName = "Audible";

    /// <summary>
    /// Titles that mean "this file was never identified" rather than naming a book.
    /// </summary>
    /// <remarks>
    /// Both clients and the server substitute one of these when a file carries no title
    /// tag. They are compared after normalisation, so punctuation and casing do not matter.
    /// </remarks>
    private static readonly string[] PlaceholderTitles =
    [
        "imported audiobook", "untitled audiobook", "untitled", "audiobook",
        "unknown", "unknown title", "unknown album", "track 1", "audio", "book"
    ];

    /// <summary>
    /// Whether an edition belongs in Explore at all.
    /// </summary>
    /// <remarks>
    /// Explore is a catalogue of recordings other listeners can look up, so an entry has to
    /// name a book. Every edition anyone scans is published by default, which meant a file
    /// with no tags arrived as "Imported audiobook" and sat in the catalogue as an entry
    /// nobody could identify or buy.
    ///
    /// An author is required for the same reason. It is also the cheapest evidence that the
    /// title came from the file's own tags rather than being guessed from a filename: a
    /// tagged audiobook essentially always names its author, and a bare download rarely
    /// does. That is a proxy rather than a proof, and the cost of it being wrong is a
    /// correctly-titled book waiting for one listener with a properly tagged copy.
    /// </remarks>
    public static bool IsPublishable(BookFingerprint fingerprint) =>
        UnpublishableReason(fingerprint) is null;

    /// <summary>
    /// Why an edition is being withheld from the catalogue, or null when it is fit to list.
    /// </summary>
    /// <remarks>
    /// Returned to administrators so a missing book can be explained rather than guessed at.
    /// </remarks>
    public static string? UnpublishableReason(BookFingerprint fingerprint)
    {
        var title = Normalize(EditionTitleFormatter.Format(fingerprint));
        if (title.Length == 0) return "The edition has no title.";
        if (PlaceholderTitles.Contains(title, StringComparer.Ordinal))
        {
            return "The title is a placeholder, so this file was never identified.";
        }
        // A title that is only digits, or a single character, names nothing.
        if (title.Length < 2) return "The title is too short to name a book.";
        if (title.All(char.IsDigit)) return "The title is only digits, so it is a filename.";
        return string.IsNullOrWhiteSpace(fingerprint.Author)
            ? "The edition has no author, so its title was probably guessed from a filename."
            : null;
    }

    /// <summary>Builds the administrative view of one edition.</summary>
    public static ExploreCatalogAdminEntry AdminEntry(
        ExploreCatalogBook book, BookFingerprint fingerprint, bool isPublished)
    {
        var reason = UnpublishableReason(fingerprint);
        return new ExploreCatalogAdminEntry(
            book,
            isPublished,
            reason is null,
            isPublished ? reason : "An administrator hid this edition.");
    }

    /// <summary>
    /// Where to buy this recording on Audible.
    /// </summary>
    /// <remarks>
    /// A product identifier gives an exact listing; without one the best that can be
    /// offered is a search, because guessing at a product URL from a title would send
    /// listeners to a page for the wrong recording.
    /// </remarks>
    public static Uri AudiblePurchaseURL(string query, string? productIdentifier) =>
        new(IsAudibleProductIdentifier(productIdentifier)
            ? $"https://www.audible.com/pd/{productIdentifier!.ToUpperInvariant()}"
            : $"https://www.audible.com/search?keywords={Uri.EscapeDataString(query)}");

    /// <summary>
    /// Whether an identifier is an Audible ASIN rather than an ISBN.
    /// </summary>
    /// <remarks>
    /// Files carry either, and the two are not interchangeable here: an ISBN in an
    /// Audible product path resolves to nothing. ASINs are ten characters and the
    /// audiobook ones begin with B, which is what separates them.
    /// </remarks>
    public static bool IsAudibleProductIdentifier(string? value) =>
        value is { Length: 10 } &&
        (value[0] is 'B' or 'b') &&
        value.All(char.IsLetterOrDigit);

    private const string FourthWingDescription = """
Twenty-year-old Violet Sorrengail was supposed to enter the Scribe Quadrant and live a quiet life among books and history. Instead, her mother orders her to compete for a place among Navarre's elite dragon riders.

Smaller and physically more fragile than the other candidates, Violet must rely on her intelligence and determination to survive a brutal war college where dragons incinerate the unworthy and rivals will kill for an advantage. Her greatest threat may be Xaden Riorson, the powerful and ruthless wingleader with every reason to hate her family.

Adapted from Rebecca Yarros's novel and produced with a full cast of actors, immersive sound effects and cinematic music.
""";
    private const string IronFlameDescription = """
Everyone expected Violet Sorrengail to die during her first year at Basgiath War College―Violet included. But Threshing was only the first impossible test meant to weed out the weak-willed, the unworthy, and the unlucky.

Now the real training begins, and Violet’s already wondering how she’ll get through. It’s not just that it’s grueling and maliciously brutal, or even that it’s designed to stretch the riders’ capacity for pain beyond endurance. It’s the new vice commandant, who’s made it his personal mission to teach Violet exactly how powerless she is–unless she betrays the man she loves.

Although Violet’s body might be weaker and frailer than everyone else’s, she still has her wits―and a will of iron. And leadership is forgetting the most important lesson Basgiath has taught her: Dragon riders make their own rules.

But a determination to survive won’t be enough this year.

Because Violet knows the real secret hidden for centuries at Basgiath War College―and nothing, not even dragon fire, may be enough to save them in the end.

Adapted from the novel and produced with a full cast of actors, immersive sound effects and cinematic music!

Copyright © 2023 by Rebecca Yarros. All rights reserved, including the right to reproduce, distribute, or transmit in any form or by any means. Recorded by arrangement with Entangled Publishing LLC. ℗ 2024 GraphicAudio LLC. All Rights Reserved.
""";
    private const string AcotarDescription = """
After killing a wolf in the woods, nineteen-year-old huntress Feyre Archeron is taken across the wall into the dangerous faerie lands of Prythian. Her captor is Tamlin, a powerful High Fae lord whose hostility gradually gives way to an unexpected connection.

As Feyre learns that the legends she was raised to fear do not tell the whole truth, she discovers that a spreading curse threatens Tamlin, his court, and the human world she left behind. Saving them will require her to confront an ancient enemy and decide how much she is willing to sacrifice.

Adapted from Sarah J. Maas's novel and performed by a full cast with cinematic music and immersive sound effects.
""";
    private const string AcotarMistAndFuryDescription = """
Feyre has returned to the Spring Court, but the trauma of her time Under the Mountain still haunts her. As she struggles to reclaim her freedom, she is drawn into a dangerous alliance that reaches across Prythian and exposes secrets that could change the fate of every court.

Adapted from Sarah J. Maas's novel and performed by a full cast with cinematic music and immersive sound effects.
""";
    private const string DungeonCrawlerCarlDescription = """
Carl and his ex-girlfriend's cat, Princess Donut, are forced into a planet-spanning dungeon crawl after Earth is transformed into a deadly televised game. To stay alive, they must survive increasingly bizarre levels, build unlikely alliances, and keep an audience entertained.

    Written by Matt Dinniman and narrated by Jeff Hays.
""";

    /// The shortest text worth presenting as a synopsis. Matches both clients.
    private const int MinimumDescriptionLength = 40;
    /// The stored column is varchar(4000).
    private const int MaximumDescriptionLength = 4000;

    /// <summary>
    /// Accepts a client-supplied synopsis, or rejects it.
    /// </summary>
    /// <remarks>
    /// The clients already drop encoder credits out of the description atoms, but this
    /// arrives over the network from a caller that can send anything, and it is shown to
    /// other listeners under "About this audiobook". The length bound has to be enforced
    /// here in any case, because the column is bounded.
    /// </remarks>
    public static string? NormalizeDescription(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length < MinimumDescriptionLength) return null;
        return trimmed.Length > MaximumDescriptionLength ? trimmed[..MaximumDescriptionLength] : trimmed;
    }

    /// <param name="storedDescription">
    /// The synopsis read from the file's own tags, when one has been reported for this
    /// edition. A book with neither this nor curated prose now gets no description at all,
    /// because the heading above it reads "About this audiobook" and generated text about
    /// AudioChoice's own features does not belong there.
    /// </param>
    public static ExploreCatalogBook Create(
        BookFingerprint fingerprint,
        ScanResult result,
        bool hasCover = false,
        string? storedDescription = null,
        string? productIdentifier = null)
    {
        var title = EditionTitleFormatter.Format(fingerprint);
        var query = string.Join(' ', new[] { title, fingerprint.Author }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var catalogID = fingerprint.Sha256[..Math.Min(24, fingerprint.Sha256.Length)].ToLowerInvariant();
        var isAcotarMistAndFury = title.Contains("A Court of Mist and Fury", StringComparison.OrdinalIgnoreCase);
        var isIronFlame = title.Contains("Iron Flame", StringComparison.OrdinalIgnoreCase);
        var isIronFlamePart2 = isIronFlame && title.Contains("Part 2 of 2", StringComparison.OrdinalIgnoreCase);
        var isFourthWing = title.Contains("Fourth Wing", StringComparison.OrdinalIgnoreCase);
        var isFourthWingPart1 = isFourthWing && title.Contains("Part 1 of 2", StringComparison.OrdinalIgnoreCase);
        var isAcotar = title.Contains("A Court of Thorns and Roses", StringComparison.OrdinalIgnoreCase);
        var isDungeonCrawlerCarl = title.Contains("Dungeon Crawler Carl", StringComparison.OrdinalIgnoreCase) &&
            fingerprint.Author?.Contains("Matt Dinniman", StringComparison.OrdinalIgnoreCase) == true;
        var purchaseURL = AudiblePurchaseURL(query, productIdentifier);
        return new ExploreCatalogBook(
            catalogID,
            title,
            fingerprint.Author,
            fingerprint.SeriesTitle,
            fingerprint.SeriesNumber,
            fingerprint.EditionType,
            fingerprint.Duration,
            fingerprint.FileType,
            result.ScanDate,
            result.ScannerVersion,
            FilterControlCount(result.Events),
            result.Events.Select(value => value.GroupID).Distinct().ToArray(),
            hasCover ? $"/v1/explore/{catalogID}/cover" : null,
            isIronFlamePart2 ? IronFlameDescription
                : isFourthWingPart1 ? FourthWingDescription
                : isAcotarMistAndFury ? AcotarMistAndFuryDescription
                : isAcotar ? AcotarDescription
                : isDungeonCrawlerCarl ? DungeonCrawlerCarlDescription
                // These five are hand-written for the launch catalogue and describe the
                // specific dramatised editions, so they stay ahead of the file's own tag.
                : NormalizeDescription(storedDescription),
            purchaseURL,
            PurchaseProviderName,
            // "Verified" means the link is known to be this exact recording, which is only
            // true when the file told us its product identifier. A search result is a guess.
            IsAudibleProductIdentifier(productIdentifier),
            NormalizedIdentifier(productIdentifier));
    }

    /// <summary>An identifier reduced so two spellings of one value compare equal.</summary>
    private static string? NormalizedIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static int FilterControlCount(IReadOnlyList<ScanEvent> events) =>
        events.Count(value => string.IsNullOrWhiteSpace(value.AggregateKey)) +
        events.Where(value => !string.IsNullOrWhiteSpace(value.AggregateKey))
            .Select(value => value.AggregateKey)
            .Distinct(StringComparer.Ordinal)
            .Count();
}
