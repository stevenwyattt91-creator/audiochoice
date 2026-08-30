using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Processing;

/// <summary>Bounds on one chapter's synthesis request.</summary>
public static class NarrationSynthesisLimits
{
    /// <summary>
    /// The longest chapter that may be submitted.
    /// </summary>
    /// <remarks>
    /// A chapter this long is roughly forty minutes of audio and about a dollar of synthesis. Past
    /// it the client has almost certainly failed to divide the book into chapters at all, and
    /// accepting it would bill a listener for that mistake.
    /// </remarks>
    public const int MaximumChapterCharacters = 40_000;
}

public enum NarrationJobStatus { Queued, Running, Completed, Failed }

/// <summary>
/// One chapter's synthesis job.
/// </summary>
/// <remarks>
/// A job rather than a synchronous response because a chapter can hold twenty thousand
/// characters, which is minutes of synthesis: the client polls instead of holding a request open
/// past every sensible timeout.
///
/// The finished audio is carried on the record and handed back in the poll response. It is
/// deliberately not written to cloud storage -- see <see cref="NarrationChapterJobs"/>.
/// </remarks>
public sealed record NarrationChapterJob(
    Guid JobID,
    Guid UserID,
    string Sha256,
    int ChapterIndex,
    string VoiceID,
    NarrationJobStatus Status,
    SynthesizedChapter? Chapter = null,
    SynthesisRoute? Route = null,
    string? Error = null,
    DateTimeOffset CreatedAt = default)
{
    public override string ToString() =>
        $"NarrationChapterJob {{ JobID = {JobID}, ChapterIndex = {ChapterIndex}, " +
        $"VoiceID = {VoiceID}, Status = {Status}, " +
        $"AudioBytes = {Chapter?.Audio.Length ?? 0} }}";
}

/// <summary>
/// Chapter synthesis jobs, held in memory only.
/// </summary>
/// <remarks>
/// **Audio is returned in the poll response and never written to cloud storage.** The design
/// originally called for a blob container and a per-account signed download URL. Returning the
/// bytes instead is both simpler and a stronger version of the promise this feature makes.
///
/// The promise is that a listener's book is not retained. Text is held for one request and
/// dropped, which the contract tests check. But chapter *audio* is derived from that text closely
/// enough that a recording of someone's book sitting in a storage container is the same
/// disclosure wearing a different coat -- and unlike the text, it would sit there indefinitely,
/// governed by a retention policy rather than by the absence of anywhere to put it. There is no
/// container to leak, no signed URL to mis-scope, and no expiry window to get wrong, because
/// there is nowhere the audio is.
///
/// The cost is that a job's result is lost if the process restarts before the client polls, and
/// that a chapter must be re-synthesized in that case. A chapter is a few megabytes and a couple
/// of minutes of work, the client already retries, and the alternative was a storage account, a
/// container, a SAS-signing path and a retention policy -- for state whose whole useful life is
/// the minute between finishing and being collected.
///
/// Jobs are swept once they are older than <see cref="RetentionMinutes"/>, so a client that never
/// collects cannot pin a chapter of audio in memory.
/// </remarks>
public sealed class NarrationChapterJobs
{
    /// <summary>
    /// How long a finished job is kept for collection.
    /// </summary>
    /// <remarks>
    /// Long enough for a client that lost signal mid-poll to come back, short enough that
    /// uncollected audio is not accumulating.
    /// </remarks>
    public const int RetentionMinutes = 30;

    /// <summary>
    /// Concurrent chapters per account.
    /// </summary>
    /// <remarks>
    /// A whole book queued at once would be a listener spending a great deal of money in one
    /// gesture. Two keeps a reader ahead of the playhead without letting a single import bill for
    /// three hundred chapters before anyone has heard one.
    /// </remarks>
    public const int MaximumActivePerAccount = 2;

    private readonly Dictionary<Guid, NarrationChapterJob> _jobs = [];
    private readonly object _gate = new();

    public NarrationChapterJob? Create(
        Guid userID,
        string sha256,
        int chapterIndex,
        string voiceID)
    {
        lock (_gate)
        {
            Sweep();

            // Re-requesting a chapter already in flight returns the same job rather than starting
            // a second one. Without this a client retrying a poll timeout would pay twice for one
            // chapter.
            var existing = _jobs.Values.FirstOrDefault(job =>
                job.UserID == userID &&
                string.Equals(job.Sha256, sha256, StringComparison.OrdinalIgnoreCase) &&
                job.ChapterIndex == chapterIndex &&
                job.VoiceID == voiceID &&
                job.Status is NarrationJobStatus.Queued or NarrationJobStatus.Running);
            if (existing is not null) return existing;

            var active = _jobs.Values.Count(job =>
                job.UserID == userID &&
                job.Status is NarrationJobStatus.Queued or NarrationJobStatus.Running);
            if (active >= MaximumActivePerAccount) return null;

            var job = new NarrationChapterJob(
                JobID: Guid.NewGuid(),
                UserID: userID,
                Sha256: sha256.ToLowerInvariant(),
                ChapterIndex: chapterIndex,
                VoiceID: voiceID,
                Status: NarrationJobStatus.Queued,
                CreatedAt: DateTimeOffset.UtcNow);
            _jobs[job.JobID] = job;
            return job;
        }
    }

    /// <summary>The job, but only for the account that created it.</summary>
    public NarrationChapterJob? Find(Guid jobID, Guid userID)
    {
        lock (_gate)
        {
            var job = _jobs.GetValueOrDefault(jobID);
            // Checked here rather than at the call site, so no endpoint can forget: a job holds a
            // chapter of somebody's book.
            return job?.UserID == userID ? job : null;
        }
    }

    public void Update(Guid jobID, Func<NarrationChapterJob, NarrationChapterJob> change)
    {
        lock (_gate)
        {
            var job = _jobs.GetValueOrDefault(jobID);
            if (job is null) return;
            _jobs[jobID] = change(job);
        }
    }

    /// <summary>Drops the audio once it has been collected, keeping the record.</summary>
    public void Collected(Guid jobID)
    {
        lock (_gate)
        {
            var job = _jobs.GetValueOrDefault(jobID);
            if (job is null) return;
            _jobs[jobID] = job with { Chapter = job.Chapter is null ? null : job.Chapter with { Audio = [] } };
        }
    }

    public int Count
    {
        get { lock (_gate) { return _jobs.Count; } }
    }

    private void Sweep()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-RetentionMinutes);
        foreach (var stale in _jobs.Values.Where(job => job.CreatedAt < cutoff).ToArray())
        {
            _jobs.Remove(stale.JobID);
        }
    }
}
