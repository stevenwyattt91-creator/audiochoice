#if POSTGRES
using Npgsql;

namespace AudioChoice.Api.Services;

public sealed class PostgresDatabaseInitializer(
    NpgsqlDataSource dataSource,
    DatabaseOptions options,
    IWebHostEnvironment environment,
    ILogger<PostgresDatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.ApplyMigrations) return;

        var migrationsPath = Path.IsPathRooted(options.MigrationsPath)
            ? options.MigrationsPath
            : Path.Combine(environment.ContentRootPath, options.MigrationsPath);
        if (!Directory.Exists(migrationsPath))
        {
            throw new DirectoryNotFoundException(
                $"Database migrations folder was not found: {migrationsPath}");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var table = connection.CreateCommand())
        {
            table.CommandText = """
                create table if not exists schema_migrations (
                    name varchar(255) primary key,
                    applied_at timestamptz not null
                );
                """;
            await table.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var path in Directory.GetFiles(migrationsPath, "*.sql").Order())
        {
            var name = Path.GetFileName(path);
            await using var check = connection.CreateCommand();
            check.CommandText = "select exists(select 1 from schema_migrations where name = $1);";
            check.Parameters.AddWithValue(name);
            if ((bool)(await check.ExecuteScalarAsync(cancellationToken) ?? false)) continue;

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migration = connection.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = await File.ReadAllTextAsync(path, cancellationToken);
            await migration.ExecuteNonQueryAsync(cancellationToken);

            await using var record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText =
                "insert into schema_migrations(name, applied_at) values ($1, now());";
            record.Parameters.AddWithValue(name);
            await record.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Applied database migration {MigrationName}.", name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
#endif
