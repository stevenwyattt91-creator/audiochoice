using AudioChoice.Api.Contracts;
using AudioChoice.Api.Services;

namespace AudioChoice.Api.Processing;

public sealed class ScanWorker(
    IScanJobQueue queue,
    IScanCatalog catalog,
    IScanPipeline pipeline,
    ITemporaryAudioStorage temporaryAudio,
    IPrivateTranscriptStore transcriptStore,
    ITransactionalEmailSender emailSender,
    IInternalAuditStore audits,
    OpenAIProcessingOptions options,
    ILogger<ScanWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        var laneTasks = Enumerable.Range(0, Math.Max(1, options.ScanWorkerConcurrency))
            .Select(_ => RunLane(stoppingToken));
        await Task.WhenAll(laneTasks);
    }

    private async Task RunLane(
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var scanID = await queue.Dequeue(stoppingToken);

            try
            {
                logger.LogInformation("Claimed scan job {ScanID}.", scanID);
                var job = catalog.FindJob(scanID);
                if (job is null || job.Status != CloudScanStatus.Queued)
                {
                    logger.LogWarning(
                        "Skipping scan job {ScanID} because it was missing or was no longer queued.",
                        scanID);
                    continue;
                }

                var upload = catalog.FindUpload(job.UploadID);
                if (upload is null)
                {
                    catalog.FailJob(scanID);
                    continue;
                }

                // A completed result before this job begins means this is a reanalysis or
                // repeat scan. Those must never generate another internal catalog alert.
                var isNewCatalogEdition = catalog.FindResult(job.Fingerprint) is null;

                catalog.SetJobStatus(scanID, CloudScanStatus.Processing);
                catalog.UpdateJobProgress(scanID, 5, "preparing");
                logger.LogInformation("Processing scan job {ScanID}.", scanID);

                using var heartbeatCancellation = CancellationTokenSource
                    .CreateLinkedTokenSource(stoppingToken);
                var heartbeat = MaintainLease(
                    scanID,
                    heartbeatCancellation.Token);

                ScanResult result;
                try
                {
                    var savedTranscript = await transcriptStore.Load(
                        upload.Fingerprint,
                        stoppingToken);
                    if (savedTranscript is not null && savedTranscript.IsComplete is not false &&
                        savedTranscript.Segments.Count > 0)
                    {
                        logger.LogInformation(
                            "Reanalyzing saved transcript for scan job {ScanID}; audio processing is skipped.",
                            scanID);
                        result = await pipeline.Process(
                            upload,
                            (percent, stage) => catalog.UpdateJobProgress(scanID, percent, stage),
                            stoppingToken,
                            (completed, total) => catalog.UpdateChunkProgress(scanID, completed, total),
                            scanID);
                    }
                    else
                    {
                        await using var materialized = await temporaryAudio.Materialize(
                            upload,
                            stoppingToken);
                        result = await pipeline.Process(
                            upload with { StoredPath = materialized.Path },
                            (percent, stage) => catalog.UpdateJobProgress(scanID, percent, stage),
                            stoppingToken,
                            (completed, total) => catalog.UpdateChunkProgress(scanID, completed, total),
                            scanID);
                    }
                }
                finally
                {
                    heartbeatCancellation.Cancel();
                    try { await heartbeat; }
                    catch (OperationCanceledException) { }
                }

                var completed = catalog.CompleteJob(scanID, result);
                if (completed && isNewCatalogEdition)
                {
                    if (audits.CreateAutomaticFocusedAssignment(scanID))
                    {
                        logger.LogInformation("Created focused audit task for scan job {ScanID}.", scanID);
                    }
                    try
                    {
                        await emailSender.SendNewCatalogScanAlert(
                            scanID,
                            upload.FileName,
                            upload.Fingerprint,
                            result,
                            stoppingToken);
                    }
                    catch (Exception notificationException)
                    {
                        // Email delivery is informational only and must never change a
                        // completed scan into a failed scan.
                        logger.LogWarning(
                            notificationException,
                            "Could not send the new catalog scan alert for scan job {ScanID}.",
                            scanID);
                    }
                }

                if (completed && !upload.IsDeleted)
                {
                    try
                    {
                        await temporaryAudio.Delete(upload, stoppingToken);
                        catalog.MarkUploadDeleted(upload.ID);
                    }
                    catch (Exception cleanupException)
                    {
                        logger.LogWarning(
                            cleanupException,
                            "Could not delete private audio for scan job {ScanID}.",
                            scanID);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                catalog.FailJob(scanID);
                logger.LogError(
                    exception,
                    "Scan job {ScanID} failed.",
                    scanID);
            }
            finally
            {
                queue.Complete(scanID);
            }
        }
    }

    private async Task MaintainLease(Guid scanID, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            queue.Renew(scanID);
        }
    }
}
