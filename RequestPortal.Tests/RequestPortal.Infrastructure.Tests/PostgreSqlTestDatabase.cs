using Microsoft.EntityFrameworkCore;
using Npgsql;
using RequestPortal.Infrastructure.Persistence;
using Xunit;

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

    public static async Task<PostgreSqlTestDatabase> CreateAsync(
        CancellationToken cancellationToken = default)
    {
        string? baseConnectionString =
            Environment.GetEnvironmentVariable("REQUESTPORTAL_POSTGRES") ??
            Environment.GetEnvironmentVariable("ConnectionStrings__RequestPortalTest");

        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            Assert.Skip(
                "PostgreSQL integration test skipped. Set REQUESTPORTAL_POSTGRES or ConnectionStrings__RequestPortalTest to run it locally.");
            throw new InvalidOperationException("Unreachable after dynamic skip.");
        }

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
            SearchPath = schemaName
        };

        DbContextOptions<RequestPortalDbContext> options =
            new DbContextOptionsBuilder<RequestPortalDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options;

        RequestPortalDbContext db = new(options);
        await db.Database.MigrateAsync(cancellationToken);

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
