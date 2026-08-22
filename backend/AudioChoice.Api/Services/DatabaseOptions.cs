namespace AudioChoice.Api.Services;

public sealed class DatabaseOptions
{
    public bool Enabled { get; init; }
    public bool ApplyMigrations { get; init; }
    public string ConnectionString { get; init; } = string.Empty;
    public string MigrationsPath { get; init; } = "Database/Migrations";
}
