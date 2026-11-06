using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RequestPortal.Common.Diagnostics;
using RequestPortal.Infrastructure.Management;
using RequestPortal.Infrastructure.Persistence;
using RequestPortal.Web.Services;
using RequestPortal.Web.Startup;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class ManagementDashboardStartupTests
{
    private sealed class DatabaseDiagnostic(bool healthy) : IStartupDiagnostic
    {
        public string Name => "PostgreSQL";
        public Task<DiagnosticCheckResult> CheckAsync(CancellationToken ct = default) => Task.FromResult(new DiagnosticCheckResult(Name, healthy));
    }
    private sealed class Refresh(Func<int, CancellationToken, Task<bool>> refresh) : IManagementDashboardAggregateRefresher
    {
        public int Calls;
        public Task<bool> RefreshAsync(CancellationToken ct = default) => refresh(Interlocked.Increment(ref Calls), ct);
    }
    private static WebApplication Create(Refresh refresh, bool healthy = true, PostgreSqlTestDatabase? fixture = null)
    {
        string root = Path.GetFullPath("../../../../../RequestPortal.Web", AppContext.BaseDirectory);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test", ContentRootPath = root,
            ApplicationName = typeof(RequestPortal.Web.Pages.HealthModel).Assembly.GetName().Name });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddRazorPages();
        builder.Services.AddSingleton<IStartupDiagnostic>(new DatabaseDiagnostic(healthy));
        builder.Services.AddScoped<StartupDiagnosticRunner>();
        builder.Services.AddSingleton<IManagementDashboardAggregateRefresher>(refresh);
        builder.Services.AddSingleton<ManagementDashboardWorkerState>();
        builder.Services.AddHostedService<ManagementDashboardAggregateWorker>();
        builder.Services.AddScoped(_ => fixture is not null ? fixture.CreateSiblingContext() : new RequestPortalDbContext(
            new DbContextOptionsBuilder<RequestPortalDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=requestportal_tests;Username=fixture;Timeout=1").Options));
        var app = builder.Build();
        app.MapRazorPages();
        app.MapManagementDashboardReadiness();
        return app;
    }
    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new(app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };

    [Fact]
    public async Task KestrelAndRealHealthRespondWithNoSnapshotWhileWorkerInitializes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PostgreSqlTestDatabase.CreateAsync(ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = new Refresh(async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return true; });
        await using var app = Create(refresh, fixture: fixture);
        await StartupTasks.CompleteAsync(app); // No refresh service call before Kestrel starts.
        Assert.Equal(0, refresh.Calls);
        await app.StartAsync(ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            using var client = Client(app);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/dashboard", ct)).StatusCode);
            var status = await ManagementDashboardReadiness.ReadAsync(fixture.Db, app.Services.GetRequiredService<ManagementDashboardWorkerState>(), ct);
            Assert.False(status.AggregateInitialized);
            Assert.Equal("Refreshing", status.Worker.Status);
            Assert.Equal(1, refresh.Calls); // Concurrent probes did not trigger another rebuild.
        }
        finally { release.TrySetResult(); await app.StopAsync(ct); }
    }

    [Fact]
    public async Task WorkerFailureDoesNotCrashKestrelAndRetriesSuccessfully()
    {
        var ct = TestContext.Current.CancellationToken;
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = new Refresh((attempt, _) =>
        {
            if (attempt == 1) { failed.TrySetResult(); throw new InvalidOperationException("Fixture refresh failure"); }
            recovered.TrySetResult(); return Task.FromResult(true);
        });
        await using var app = Create(refresh);
        await app.StartAsync(ct);
        try
        {
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            using var client = Client(app);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", ct)).StatusCode);
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            var state = app.Services.GetRequiredService<ManagementDashboardWorkerState>();
            for (int i=0; i<100 && state.Current.LastPublishedUtc is null; i++) await Task.Delay(10, ct);
            Assert.NotNull(state.Current.LastPublishedUtc);
            Assert.Null(state.Current.FailureType);
            Assert.NotNull(state.Current.LastFailureUtc);
            Assert.InRange(refresh.Calls, 2, 3);
        }
        finally { await app.StopAsync(ct); }
    }

    [Fact]
    public async Task DatabaseDiagnosticsStillBlockStartupAndReportUnhealthy()
    {
        var ct = TestContext.Current.CancellationToken;
        var refresh = new Refresh((_, _) => Task.FromResult(false));
        await using var app = Create(refresh, healthy: false);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StartupTasks.CompleteAsync(app));
        Assert.Contains("RP-STARTUP-DB-001", error.Message);
        Assert.Equal(0, refresh.Calls);
        var model = new RequestPortal.Web.Pages.HealthModel([new DatabaseDiagnostic(false)], app.Configuration, app.Environment);
        Assert.Equal(503, Assert.IsType<Microsoft.AspNetCore.Mvc.JsonResult>(await model.OnGetAsync(ct)).StatusCode);
        await Assert.ThrowsAnyAsync<Npgsql.NpgsqlException>(() => StartupTasks.ApplyDatabaseMigrationsAsync(app));
    }
}
