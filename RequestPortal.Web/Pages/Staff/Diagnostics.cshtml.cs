using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Administration;
using RequestPortal.Common.Diagnostics;
using RequestPortal.Domain.Integration;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Web.Pages.Staff;

public sealed class DiagnosticsModel : PageModel
{
    private readonly StartupDiagnosticRunner diagnosticRunner;
    private readonly IStaffOperationsService staffOperationsService;
    private readonly RequestPortalDbContext dbContext;

    public DiagnosticsModel(
        StartupDiagnosticRunner diagnosticRunner,
        IStaffOperationsService staffOperationsService,
        RequestPortalDbContext dbContext)
    {
        this.diagnosticRunner = diagnosticRunner;
        this.staffOperationsService = staffOperationsService;
        this.dbContext = dbContext;
    }

    public IReadOnlyList<DiagnosticCheckResult> Results { get; private set; } = [];

    public async Task OnGetAsync()
    {
        List<DiagnosticCheckResult> results = (await diagnosticRunner.RunAsync(HttpContext.RequestAborted)).ToList();
        StaffOperationsSnapshot operations = await staffOperationsService.GetSnapshotAsync(HttpContext.RequestAborted);

        results.Add(new DiagnosticCheckResult(
            "Outbox dead letters",
            operations.DeadLetters.Count == 0,
            operations.DeadLetters.Count == 0
                ? "No RequestPortal outbox messages are currently dead-lettered."
                : $"SF-RP-MSG-001: {operations.DeadLetters.Count} outbox message(s) exhausted automatic retries and require staff review."));

        results.Add(new DiagnosticCheckResult(
            "Notification delivery failures",
            operations.FailedNotificationDeliveries.Count == 0,
            operations.FailedNotificationDeliveries.Count == 0
                ? "No recent failed or dead-lettered notification deliveries are recorded."
                : $"SF-RP-NOTIFY-001: {operations.FailedNotificationDeliveries.Count} recent notification delivery failure(s) require review."));

        int failedIntegrations = await dbContext.IntegrationLinks
            .AsNoTracking()
            .CountAsync(link => link.Status == SynchronizationStatus.Failed, HttpContext.RequestAborted);

        results.Add(new DiagnosticCheckResult(
            "Integration failures",
            failedIntegrations == 0,
            failedIntegrations == 0
                ? "No integration links are currently in a failed state."
                : $"SF-RP-INT-001: {failedIntegrations} integration link(s) are failed and require retry or reconciliation."));

        Results = results;
    }
}
