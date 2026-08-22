#if POSTGRES
using Npgsql;

namespace AudioChoice.Api.Processing;

public sealed class PostgresScanJobQueue(
    NpgsqlDataSource dataSource,
    OpenAIProcessingOptions options) : IScanJobQueue
{
    private readonly string _leaseOwner = $"{Environment.MachineName}-{Guid.NewGuid():N}";

    // The job is already committed to PostgreSQL before this method is called.
    public bool TryQueue(Guid scanID) => true;

    public async ValueTask<Guid> Dequeue(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using (var exhausted = new NpgsqlCommand("""
                update scan_jobs set status = 'failed',
                    last_error = 'Maximum worker attempts reached',
                    lease_owner = null, lease_expires_at = null, updated_at = now()
                where attempt_count >= $1
                    and (
                    status = 'queued' or (
                        status = 'processing'
                        and (lease_expires_at is null or lease_expires_at <= now())
                    ))
                    and processing_lane = $2;
                """, connection))
            {
                exhausted.Parameters.AddWithValue(Math.Max(1, options.MaximumJobAttempts));
                exhausted.Parameters.AddWithValue(options.ProcessingLane);
                await exhausted.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var command = new NpgsqlCommand("""
                with next_job as (
                    select id from scan_jobs
                    where (
                        (
                            status = 'queued' and available_at <= now() and attempt_count < $2
                        ) or (
                            status = 'processing'
                            and attempt_count < $2
                            and (lease_expires_at is null or lease_expires_at <= now())
                        )
                    )
                    and processing_lane = $3
                    order by available_at, created_at
                    for update skip locked
                    limit 1
                )
                update scan_jobs job set
                    status = 'queued',
                    lease_owner = $1,
                    lease_expires_at = now() + interval '5 minutes',
                    updated_at = now()
                from next_job
                where job.id = next_job.id
                returning job.id;
                """, connection);
            command.Parameters.AddWithValue(_leaseOwner);
            command.Parameters.AddWithValue(Math.Max(1, options.MaximumJobAttempts));
            command.Parameters.AddWithValue(options.ProcessingLane);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            if (value is Guid scanID) return scanID;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        throw new OperationCanceledException(cancellationToken);
    }

    public void Complete(Guid scanID)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            update scan_jobs set lease_owner = null, lease_expires_at = null, updated_at = now()
            where id = $1 and lease_owner = $2;
            """, connection);
        command.Parameters.AddWithValue(scanID);
        command.Parameters.AddWithValue(_leaseOwner);
        command.ExecuteNonQuery();
    }

    public void Renew(Guid scanID)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            update scan_jobs set lease_expires_at = now() + interval '5 minutes',
                updated_at = now()
            where id = $1 and lease_owner = $2 and status = 'processing';
            """, connection);
        command.Parameters.AddWithValue(scanID);
        command.Parameters.AddWithValue(_leaseOwner);
        command.ExecuteNonQuery();
    }
}
#endif
