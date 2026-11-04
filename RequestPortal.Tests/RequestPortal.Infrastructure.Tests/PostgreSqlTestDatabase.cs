using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Tests;

internal sealed class PostgreSqlTestDatabase : IAsyncDisposable
{
    private readonly string adminConnectionString;
    private readonly string schemaName;

    private PostgreSqlTestDatabase(
        string adminConnectionString,
        string schemaName,
        RequestPortalDbContext db)
    {
        this.adminConnectionString = adminConnectionString;
        this.schemaName = schemaName;
        Db = db;
    }

    public RequestPortalDbContext Db { get; }

    public RequestPortalDbContext CreateSiblingContext()
    {
        // Open connections redact passwords. Use the original isolated fixture configuration,
        // keeping PersistSecurityInfo disabled and never exposing credentials to test output.
        var settings = new NpgsqlConnectionStringBuilder(adminConnectionString) { SearchPath = schemaName, Pooling = false };
        return new(new DbContextOptionsBuilder<RequestPortalDbContext>().UseNpgsql(settings.ConnectionString).Options);
    }

    public static async Task<PostgreSqlTestDatabase> CreateAsync(
        CancellationToken cancellationToken = default, string? targetMigration = null)
    {
        string baseConnectionString =
            Environment.GetEnvironmentVariable("REQUESTPORTAL_POSTGRES")
            ?? throw new InvalidOperationException(
                "REQUESTPORTAL_POSTGRES must be configured for Request Portal PostgreSQL integration tests.");

        string schemaName = "rp_test_" + Guid.NewGuid().ToString("N");

        await using (NpgsqlConnection admin = new(baseConnectionString))
        {
            await admin.OpenAsync(cancellationToken);
            await using NpgsqlCommand create = admin.CreateCommand();
            create.CommandText = $"CREATE SCHEMA \"{schemaName}\"";
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        NpgsqlConnectionStringBuilder builder = new(baseConnectionString)
        {
            SearchPath = schemaName,
            // Every test uses a unique schema/connection string. Retaining a pool per
            // schema exhausts PostgreSQL connections as the integration suite grows.
            Pooling = false
        };

        DbContextOptions<RequestPortalDbContext> options =
            new DbContextOptionsBuilder<RequestPortalDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options;

        RequestPortalDbContext db = new(options);
        await db.GetService<IMigrator>().MigrateAsync(targetMigration,cancellationToken);

        return new PostgreSqlTestDatabase(
            baseConnectionString,
            schemaName,
            db);
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();

        await using NpgsqlConnection admin = new(adminConnectionString);
        await admin.OpenAsync();

        await using NpgsqlCommand drop = admin.CreateCommand();
        drop.CommandText = $"DROP SCHEMA IF EXISTS \"{schemaName}\" CASCADE";
        await drop.ExecuteNonQueryAsync();
    }
}
