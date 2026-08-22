using AudioChoice.Api.Services;

namespace AudioChoice.Api.Processing;

/// <summary>Deletes expired relay blobs; companion transfers are never archival storage.</summary>
public sealed class CompanionTransferCleanupService(
    ICompanionTransferStore transfers,
    ICompanionTransferStorage storage,
    ILogger<CompanionTransferCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Clean(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (await timer.WaitForNextTickAsync(stoppingToken)) await Clean(stoppingToken);
    }

    private async Task Clean(CancellationToken cancellationToken)
    {
        foreach (var transfer in transfers.Expired(DateTimeOffset.UtcNow))
        {
            try { await storage.Delete(transfer, cancellationToken); transfers.MarkDeleted(transfer.ID); }
            catch (Exception exception) { logger.LogWarning(exception, "Could not delete expired companion transfer {TransferID}.", transfer.ID); }
        }
    }
}
