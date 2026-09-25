using Microsoft.EntityFrameworkCore;
using RequestPortal.Common.Diagnostics;
using RequestPortal.Infrastructure.Persistence;
using RequestPortal.Web.Demo;

namespace RequestPortal.Web.Startup;

internal static class StartupTasks
{
    public static async Task RunAsync(WebApplication app)
    {
        await ApplyDatabaseMigrationsAsync(app);
        await PrepareDemoAsync(app);
        await ReportStartupDiagnosticsAsync(app);
    }

    private static async Task ApplyDatabaseMigrationsAsync(
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

        await db.Database.MigrateAsync();
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
