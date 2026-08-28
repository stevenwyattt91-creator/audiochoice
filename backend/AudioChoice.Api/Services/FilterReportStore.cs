using System.Text.Json;
using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

/// <summary>
/// Where listener reports about wrong filtering are kept.
/// </summary>
public interface IFilterReportStore
{
    FilterReport? Record(Guid userID, FilterReportRequest request);

    /// <summary>Newest first, for triage.</summary>
    IReadOnlyList<FilterReport> List(int limit = 200, BookFingerprint? fingerprint = null);
}

/// <summary>
/// Shared validation, so the file and database stores cannot disagree about what a
/// well-formed report is.
/// </summary>
public static class FilterReports
{
    /// <summary>
    /// How much audio before the tap a report covers, when the client does not say.
    /// </summary>
    /// <remarks>
    /// A listener hears something, registers it, finds the button and taps. Twenty seconds
    /// is generous enough to contain the passage without sweeping in so much that triage
    /// cannot tell what was meant.
    /// </remarks>
    public const double DefaultWindowSeconds = 20;

    /// <summary>Longer than this is a complaint about the book, not a moment in it.</summary>
    public const double MaximumWindowSeconds = 120;

    public static FilterReport? Validate(Guid userID, FilterReportRequest request)
    {
        if (userID == Guid.Empty) return null;
        if (request.Fingerprint is null) return null;
        if (string.IsNullOrWhiteSpace(request.Fingerprint.Sha256)) return null;
        if (!double.IsFinite(request.PositionSeconds) || request.PositionSeconds < 0) return null;
        if (!Enum.IsDefined(request.Kind)) return null;

        var window = request.WindowSeconds ?? DefaultWindowSeconds;
        if (!double.IsFinite(window) || window <= 0) window = DefaultWindowSeconds;
        window = Math.Min(window, MaximumWindowSeconds);

        return new FilterReport(
            Guid.NewGuid(),
            userID,
            request.Fingerprint,
            request.Kind,
            request.PositionSeconds,
            window,
            string.IsNullOrWhiteSpace(request.ScannerVersion) ? null : request.ScannerVersion!.Trim(),
            request.ScanEventID,
            request.CategoryID,
            DateTimeOffset.UtcNow);
    }
}

/// <summary>
/// Local-development adapter for the same contract the production store implements.
/// </summary>
public sealed class FileFilterReportStore : IFilterReportStore
{
    private readonly string _path;
    private readonly List<FilterReport> _reports = [];
    private readonly object _gate = new();

    public FileFilterReportStore(AudioChoiceDataPaths dataPaths)
        : this(dataPaths.FilterReports)
    {
    }

    public FileFilterReportStore(string path)
    {
        _path = path;
        Load();
    }

    public FilterReport? Record(Guid userID, FilterReportRequest request)
    {
        var report = FilterReports.Validate(userID, request);
        if (report is null) return null;
        lock (_gate)
        {
            _reports.Add(report);
            Persist();
        }
        return report;
    }

    public IReadOnlyList<FilterReport> List(int limit = 200, BookFingerprint? fingerprint = null)
    {
        lock (_gate)
        {
            IEnumerable<FilterReport> values = _reports;
            if (fingerprint is not null)
            {
                var key = InMemoryScanCatalog.FingerprintKey(fingerprint);
                values = values.Where(value =>
                    InMemoryScanCatalog.FingerprintKey(value.Fingerprint) == key);
            }
            return values
                .OrderByDescending(value => value.ReportedAt)
                .Take(Math.Clamp(limit, 1, 1000))
                .ToArray();
        }
    }

    private void Load()
    {
        if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path)) return;
        try
        {
            var state = JsonSerializer.Deserialize<List<FilterReport>>(File.ReadAllText(_path));
            if (state is not null) _reports.AddRange(state);
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            // Reports are diagnostics. A damaged file must not stop the API from starting,
            // and starting without them is better than refusing to accept new ones.
        }
    }

    private void Persist()
    {
        if (string.IsNullOrWhiteSpace(_path)) return;
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_reports));
        File.Move(temporary, _path, true);
    }
}
