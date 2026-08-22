using AudioChoice.Api.Services;

namespace AudioChoice.Api.Processing;

public sealed class TemporaryAudioCleanupService(
    IScanCatalog catalog,
    ITemporaryAudioStorage temporaryAudio,
    ILogger<TemporaryAudioCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Clean(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await Clean(stoppingToken);
        }
    }

    private async Task Clean(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var upload in catalog.ExpiredUploads(DateTimeOffset.UtcNow))
        {
            try
            {
                await temporaryAudio.Delete(upload, cancellationToken);
                catalog.MarkUploadDeleted(upload.ID);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Could not delete expired temporary audio upload {UploadID}.",
                    upload.ID);
            }
        }
    }
}
