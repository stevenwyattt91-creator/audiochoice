using System.Collections.Concurrent;
using System.Globalization;
using AudioChoice.Api.Contracts;
using AudioChoice.Api.Processing;

namespace AudioChoice.Api.Services;

public interface IAuditReviewClipPrewarmer
{
    void Queue(Guid assignmentID);
}

// Builds small private clips ahead of time after an administrator attaches the
// source M4B. Auditors can then start playback immediately, without waiting for
// the full audiobook to be downloaded and trimmed on their first click.
public sealed class AuditReviewClipPrewarmer(
    IInternalAuditStore audits,
    IAuditReviewMediaStorage media,
    IProcessRunner processes,
    FfmpegAudioChunkerOptions ffmpeg,
    ILogger<AuditReviewClipPrewarmer> logger) : IAuditReviewClipPrewarmer
{
    private readonly ConcurrentDictionary<Guid, byte> queued = new();

    public void Queue(Guid assignmentID)
    {
        if (!media.IsAvailable || !queued.TryAdd(assignmentID, 0)) return;
        _ = Task.Run(async () =>
        {
            try { await Prepare(assignmentID); }
            catch (Exception exception) { logger.LogError(exception, "Could not prebuild review clips for audit {AssignmentID}.", assignmentID); }
            finally { queued.TryRemove(assignmentID, out _); }
        });
    }

    private async Task Prepare(Guid assignmentID)
    {
        var workspace = audits.Workspace(assignmentID, Guid.Empty, true);
        var source = audits.ReviewSource(assignmentID);
        if (workspace is null || source is null) return;

        await using var materialized = await media.MaterializeSource(source.ObjectName, CancellationToken.None);
        foreach (var candidate in workspace.Candidates)
        {
            if (audits.ReviewClip(assignmentID, candidate.ID) is not null) continue;
            var from = Math.Max(0, candidate.StartSeconds - 15);
            var to = candidate.EndSeconds + 15;
            var output = Path.Combine(Path.GetTempPath(), $"audiochoice-audit-clip-{Guid.NewGuid():N}.m4a");
            try
            {
                var result = await processes.Run(ffmpeg.FfmpegPath,
                    ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-ss", from.ToString(CultureInfo.InvariantCulture), "-to", to.ToString(CultureInfo.InvariantCulture), "-i", materialized.Path, "-vn", "-c:a", "aac", "-b:a", "96k", output],
                    CancellationToken.None);
                if (result.ExitCode != 0 || !File.Exists(output))
                {
                    logger.LogWarning("Could not prebuild review clip {CandidateID} for audit {AssignmentID}.", candidate.ID, assignmentID);
                    continue;
                }
                await using var input = File.OpenRead(output);
                var stored = await media.StoreClip(assignmentID, candidate.ID, input, CancellationToken.None);
                audits.SaveReviewClip(assignmentID, candidate.ID, new AuditReviewClip(stored.ObjectName, from, to));
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not prebuild review clip {CandidateID} for audit {AssignmentID}.", candidate.ID, assignmentID);
            }
            finally
            {
                if (File.Exists(output)) File.Delete(output);
            }
        }
    }
}
