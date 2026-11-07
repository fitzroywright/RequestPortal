using Microsoft.EntityFrameworkCore;
using RequestPortal.Common.Diagnostics;
using RequestPortal.Infrastructure.Persistence;
using RequestPortal.Web.Demo;

namespace RequestPortal.Web.Startup;

internal static class StartupTasks
{
    public static async Task ApplyDatabaseMigrationsAsync(
        WebApplication app)
    {
        await using AsyncServiceScope scope =
            app.Services.CreateAsyncScope();

        RequestPortalDbContext db =
            scope.ServiceProvider
                .GetRequiredService<RequestPortalDbContext>();

        string[] migrations =
            db.Database.GetMigrations().ToArray();

        if (migrations.Length == 0)
        {
            throw new InvalidOperationException(
                "RP-MIGRATION-001: No EF Core migrations exist. Startup is blocked until a baseline migration is generated and reviewed.");
        }

        if (app.Environment.IsProduction() && !app.Configuration.GetValue("Demo:Enabled", false))
        {
            var pending = (await db.Database.GetPendingMigrationsAsync()).ToArray();
            if (pending.Any(x => x.EndsWith("_PostgresRequestLocations", StringComparison.Ordinal)))
                throw new InvalidOperationException("RP-LOCATION-RECOVERY-001: Verified backup and one-time geography import are required before this production release can start.");
            var recovered = await db.RequestLocationImportStates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1);
            if (recovered is null || recovered.VerifiedUtc == default || recovered.SourceFingerprint.Length != 64 ||
                !recovered.SourceFingerprint.All(Uri.IsHexDigit))
                throw new InvalidOperationException("RP-LOCATION-RECOVERY-002: Verified legacy geography import evidence is required before production startup.");
        }
        await db.Database.MigrateAsync();
    }

    public static async Task CompleteAsync(
        WebApplication app)
    {
        await PrepareDemoAsync(app);
        await ReportStartupDiagnosticsAsync(app);
    }

    private static async Task PrepareDemoAsync(
        WebApplication app)
    {
        if (!app.Configuration.GetValue("Demo:Enabled", false))
        {
            return;
        }

        await using AsyncServiceScope scope =
            app.Services.CreateAsyncScope();

        await scope.ServiceProvider
            .GetRequiredService<DemoDatabaseBootstrapper>()
            .InitializeAsync();

        await scope.ServiceProvider
            .GetRequiredService<DemoDataSeeder>()
            .SeedAsync();

        await scope.ServiceProvider
            .GetRequiredService<DemoReadinessValidator>()
            .ValidateAsync();
    }

    private static async Task ReportStartupDiagnosticsAsync(
        WebApplication app)
    {
        await using AsyncServiceScope scope =
            app.Services.CreateAsyncScope();

        IReadOnlyList<DiagnosticCheckResult> results =
            await scope.ServiceProvider
                .GetRequiredService<StartupDiagnosticRunner>()
                .RunAsync();

        DiagnosticCheckResult? databaseResult =
            results.FirstOrDefault(
                result => result.Name == "PostgreSQL");

        if (databaseResult is not null &&
            !databaseResult.IsHealthy)
        {
            throw new InvalidOperationException(
                $"RP-STARTUP-DB-001: PostgreSQL startup diagnostic failed. {databaseResult.Detail}");
        }
    }
}
