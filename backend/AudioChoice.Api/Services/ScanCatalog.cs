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
    (byte[] Bytes, string ContentType)? FindExploreCover(string catalogID);
    bool HideExploreBook(string catalogID);
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
    private readonly ConcurrentDictionary<Guid, HashSet<Guid>> _jobSubscribers = new();
    private readonly object _subscriberLock = new();
    private readonly string? _storagePath;
    private readonly object _persistenceLock = new();

    public InMemoryScanCatalog(string? storagePath = null)
    {
        _storagePath = storagePath;
        Load();
    }

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
        var existing = FindActiveJob(fingerprint);
        if (existing is not null)
        {
            AddSubscriber(existing.ID, ownerUserID);
            return existing with { OwnerUserID = ownerUserID };
        }

        var key = FingerprintKey(fingerprint);
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

    public IReadOnlyList<ExploreCatalogBook> ListExploreBooks() => _jobs.Values
        .Where(value => value.Status == CloudScanStatus.Completed && value.Result is not null)
        .GroupBy(value => FingerprintKey(value.Fingerprint))
        .Select(group => group.OrderByDescending(value => value.Result!.ScanDate).First())
        .Where(value => !string.IsNullOrWhiteSpace(value.Fingerprint.WorkTitle))
        .Where(value => !value.Fingerprint.WorkTitle!.Contains("Iron Flame", StringComparison.OrdinalIgnoreCase))
        .Select(value => ExploreCatalog.Create(
            value.Fingerprint,
            value.Result!,
            _exploreCovers.ContainsKey(value.Fingerprint.Sha256[..Math.Min(24, value.Fingerprint.Sha256.Length)].ToLowerInvariant())))
        .Where(value => !_hiddenExploreBooks.ContainsKey(value.CatalogID))
        .OrderBy(value => value.Title)
        .ToArray();

    public bool SaveExploreCover(string catalogID, byte[] imageBytes, string contentType, bool replaceExisting = false)
    {
        if (string.IsNullOrWhiteSpace(catalogID) || imageBytes.Length == 0) return false;
        _exploreCovers[catalogID.Trim().ToLowerInvariant()] = (imageBytes, contentType);
        return true;
    }

    public bool SaveEditionCover(BookFingerprint fingerprint, byte[] imageBytes, string contentType, bool replaceExisting = false) =>
        SaveExploreCover(fingerprint.Sha256[..Math.Min(24, fingerprint.Sha256.Length)], imageBytes, contentType, replaceExisting);

    public (byte[] Bytes, string ContentType)? FindExploreCover(string catalogID) =>
        _exploreCovers.TryGetValue(catalogID.Trim().ToLowerInvariant(), out var cover) ? cover : null;

    public bool HideExploreBook(string catalogID)
    {
        if (string.IsNullOrWhiteSpace(catalogID)) return false;
        _hiddenExploreBooks[catalogID.Trim().ToLowerInvariant()] = 0;
        return true;
    }

    public IReadOnlyList<BookFingerprint> ListFingerprints() =>
        _uploads.Values
            .Select(value => value.Fingerprint)
            .Concat(_jobs.Values.Select(value => value.Fingerprint))
            .GroupBy(FingerprintKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

    public bool UpdateEditionMetadata(AdminEditionMetadataRequest request) =>
        _results.ContainsKey(FingerprintKey(request.Fingerprint));

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
    public static string CanonicalKey(ExploreCatalogBook book)
    {
        var title = Normalize(book.Title);
        var author = Normalize(book.Author);
        var part = System.Text.RegularExpressions.Regex.Match(title, @"part\s*(\d+)\s*of\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (part.Success) title = title[..part.Index].Trim();
        return $"{title}|{author}|{(part.Success ? part.Value : "")}";
    }

    private static string Normalize(string? value) =>
        System.Text.RegularExpressions.Regex.Replace(value ?? "", @"[^a-z0-9]+", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim().ToLowerInvariant();

    private const string AcotarPart1URL = "https://www.graphicaudio.net/a-court-of-thorns-and-roses-1-a-court-of-thorns-and-roses-1-of-2.html";
    private const string AcotarPart2URL = "https://www.graphicaudio.net/a-court-of-thorns-and-roses-1-a-court-of-thorns-and-roses-2-of-2.html";
    private const string IronFlamePart2URL = "https://www.graphicaudio.net/the-empyrean-2-iron-flame-2-of-2.html";
    private const string FourthWingPart1URL = "https://www.graphicaudio.net/the-empyrean-1-fourth-wing-1-of-2.html";
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

    private static string DefaultDescription(string title, string? author, string? editionType) =>
        $"{title}{(string.IsNullOrWhiteSpace(author) ? "" : $" by {author}")} is available as a private AudioChoice scan. " +
        $"This {(string.IsNullOrWhiteSpace(editionType) ? "audiobook edition" : editionType.ToLowerInvariant())} includes chapter-aware playback and personalized content controls.";

    public static ExploreCatalogBook Create(BookFingerprint fingerprint, ScanResult result, bool hasCover = false)
    {
        var title = EditionTitleFormatter.Format(fingerprint);
        var query = string.Join(' ', new[] { title, fingerprint.Author }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var catalogID = fingerprint.Sha256[..Math.Min(24, fingerprint.Sha256.Length)].ToLowerInvariant();
        var isAcotarMistAndFury = title.Contains("A Court of Mist and Fury", StringComparison.OrdinalIgnoreCase);
        var isIronFlame = title.Contains("Iron Flame", StringComparison.OrdinalIgnoreCase);
        var isIronFlamePart2 = isIronFlame && title.Contains("Part 2 of 2", StringComparison.OrdinalIgnoreCase);
        var isFourthWing = title.Contains("Fourth Wing", StringComparison.OrdinalIgnoreCase);
        var isFourthWingPart1 = isFourthWing && title.Contains("Part 1 of 2", StringComparison.OrdinalIgnoreCase);
        var isKnownGraphicAudioTitle = title.Contains("A Court of Thorns and Roses", StringComparison.OrdinalIgnoreCase) ||
            isAcotarMistAndFury ||
            isIronFlamePart2 || isFourthWingPart1;
        var isGraphicAudio = isKnownGraphicAudioTitle ||
            fingerprint.EditionType?.Contains("dramatized", StringComparison.OrdinalIgnoreCase) == true ||
            fingerprint.EditionType?.Contains("graphic audio", StringComparison.OrdinalIgnoreCase) == true ||
            fingerprint.EditionType?.Contains("graphicaudio", StringComparison.OrdinalIgnoreCase) == true;
        var isAcotar = title.Contains("A Court of Thorns and Roses", StringComparison.OrdinalIgnoreCase);
        var isDungeonCrawlerCarl = title.Contains("Dungeon Crawler Carl", StringComparison.OrdinalIgnoreCase) &&
            fingerprint.Author?.Contains("Matt Dinniman", StringComparison.OrdinalIgnoreCase) == true;
        var isAcotarPart1 = isAcotar && title.Contains("Part 1 of 2", StringComparison.OrdinalIgnoreCase);
        var isAcotarPart2 = isAcotar && title.Contains("Part 2 of 2", StringComparison.OrdinalIgnoreCase);
        var provider = isGraphicAudio ? "GraphicAudio" : "Libro.fm";
        var verifiedPurchaseURL = isIronFlamePart2 ? IronFlamePart2URL
            : isFourthWingPart1 ? FourthWingPart1URL
            : isAcotarPart1 ? AcotarPart1URL
            : isAcotarPart2 ? AcotarPart2URL
            : null;
        var purchaseURL = new Uri(verifiedPurchaseURL ?? (isGraphicAudio
            ? $"https://www.graphicaudio.net/catalogsearch/result/?q={Uri.EscapeDataString(query)}"
            : $"https://libro.fm/search?q={Uri.EscapeDataString(query)}"));
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
                : DefaultDescription(title, fingerprint.Author, fingerprint.EditionType),
            purchaseURL,
            provider,
            verifiedPurchaseURL is not null);
    }

    private static int FilterControlCount(IReadOnlyList<ScanEvent> events) =>
        events.Count(value => string.IsNullOrWhiteSpace(value.AggregateKey)) +
        events.Where(value => !string.IsNullOrWhiteSpace(value.AggregateKey))
            .Select(value => value.AggregateKey)
            .Distinct(StringComparer.Ordinal)
            .Count();
}
