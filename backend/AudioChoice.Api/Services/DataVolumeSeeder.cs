using System.Net;
using Amazon.S3;
using Amazon.S3.Model;

namespace AudioChoice.Api.Services;

/// <summary>
/// Copies the durable contents of the shared data volume out of S3 on first start, for a
/// deployment whose volume is new and empty.
/// </summary>
/// <remarks>
/// This exists for one file. <see cref="FileEditionSignatureStore"/> is registered even when the
/// database is enabled, so <c>edition-signatures.json</c> -- the client-reported product
/// identifiers, narrators and chapter offsets that let a converted or re-tagged copy of an
/// audiobook find the filter results already paid for -- lives only on the volume. There is no
/// table behind it and nothing regenerates it. Losing it degrades edition matching to comparing
/// fingerprint metadata until every client happens to report again.
///
/// It is seeded from S3 rather than copied in directly because of how EFS is reachable: only from
/// inside the VPC, and the runtime image carries no AWS CLI or NFS client to do it from a one-off
/// task. The application already holds an S3 client and already mounts the volume, so it is the
/// one process positioned to do this without new tooling.
///
/// Deliberately conservative. It runs once at startup, only for files that are absent, and treats
/// a missing seed object as the ordinary case rather than an error -- which is exactly what a
/// deployment that has been running for a while looks like. It will not overwrite a file the
/// running system has already written, so leaving the configuration in place after the migration
/// is harmless.
/// </remarks>
public sealed class DataVolumeSeeder(
    IAmazonS3 client,
    TemporaryAudioStorageOptions storage,
    AudioChoiceDataPaths paths,
    DataVolumeSeedOptions options,
    ILogger<DataVolumeSeeder> logger)
{
    /// <summary>
    /// The volume files worth seeding, and the only ones.
    /// </summary>
    /// <remarks>
    /// The other files <see cref="AudioChoiceDataPaths"/> defines -- accounts, user library, user
    /// data, entitlements, affiliates, companion transfers, conversion consents, scan catalog --
    /// are the no-database fallbacks. In a Postgres deployment they are not read, so seeding them
    /// would plant stale copies of tables that are already authoritative elsewhere. That is worse
    /// than not seeding them at all.
    /// </remarks>
    private IReadOnlyList<(string Name, string Destination)> Files =>
    [
        ("edition-signatures.json", paths.EditionSignatures),
        ("edition-aliases.json", paths.EditionAliases)
    ];

    /// <summary>
    /// Restores any absent seeded file.
    /// </summary>
    /// <remarks>
    /// Awaited from <c>Program.cs</c> after the host is built but before it runs, rather than
    /// registered as an <c>IHostedService</c>. <see cref="FileEditionSignatureStore"/> reads its
    /// file in its own constructor, and the container builds every hosted service's constructor
    /// before calling any of their <c>StartAsync</c> methods -- so as a hosted service this could
    /// be ordered after something that had already loaded the empty file it was meant to restore.
    /// Running before the host removes the question.
    /// </remarks>
    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled) return;
        if (string.IsNullOrWhiteSpace(storage.S3BucketName))
        {
            logger.LogWarning(
                "Data volume seeding is enabled but no S3 bucket is configured. Skipping.");
            return;
        }

        foreach (var (name, destination) in Files)
        {
            try
            {
                await Seed(name, destination, cancellationToken);
            }
            catch (Exception error)
            {
                // Never fatal. A failure here costs edition-matching quality until the file is
                // restored by hand; refusing to start would cost the whole service.
                logger.LogError(
                    error,
                    "Could not seed {File} from the data volume seed prefix. The API is starting without it.",
                    name);
            }
        }
    }

    private async Task Seed(string name, string destination, CancellationToken cancellationToken)
    {
        if (File.Exists(destination))
        {
            logger.LogInformation(
                "{File} is already present on the data volume; leaving it alone.", name);
            return;
        }

        var key = $"{options.Prefix.TrimEnd('/')}/{name}";
        GetObjectResponse response;
        try
        {
            response = await client.GetObjectAsync(
                new GetObjectRequest { BucketName = storage.S3BucketName, Key = key },
                cancellationToken);
        }
        catch (AmazonS3Exception missing) when (missing.StatusCode == HttpStatusCode.NotFound)
        {
            // The expected state for any deployment that is not a migration.
            logger.LogInformation("No seed object at {Key}; nothing to restore.", key);
            return;
        }

        using (response)
        {
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            // Written to a temporary name and moved, matching how the stores themselves write, so
            // a seed interrupted halfway cannot leave a truncated file that parses as empty and is
            // then treated as the real thing.
            var temporary = $"{destination}.{Guid.NewGuid():N}.seed";
            try
            {
                await response.WriteResponseStreamToFileAsync(temporary, false, cancellationToken);
                File.Move(temporary, destination, overwrite: false);
                logger.LogInformation(
                    "Restored {File} onto the data volume from {Key}.", name, key);
            }
            catch (IOException) when (File.Exists(destination))
            {
                // Another replica won the race. Its copy is as good as this one.
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                throw;
            }
        }
    }
}

public sealed class DataVolumeSeedOptions
{
    /// <summary>
    /// Whether to restore absent data-volume files from S3 at startup. Intended to be on for the
    /// first deployment onto a new volume and harmless to leave on afterwards, since seeding never
    /// replaces a file that exists.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>Bucket prefix the migration uploaded the volume files to.</summary>
    public string Prefix { get; init; } = "data-volume-seed";
}
