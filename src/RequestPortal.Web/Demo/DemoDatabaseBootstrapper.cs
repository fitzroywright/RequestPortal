using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Web.Demo;

public sealed class DemoDatabaseBootstrapper
{
    private readonly RequestPortalDbContext dbContext;
    private readonly IConfiguration configuration;
    private readonly ILogger<DemoDatabaseBootstrapper> logger;

    public DemoDatabaseBootstrapper(
        RequestPortalDbContext dbContext,
        IConfiguration configuration,
        ILogger<DemoDatabaseBootstrapper> logger)
    {
        this.dbContext = dbContext;
        this.configuration = configuration;
        this.logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue("Demo:Enabled", false) ||
            !configuration.GetValue("Demo:EnsureDatabase", false))
        {
            return;
        }

        await dbContext.Database.EnsureCreatedAsync(cancellationToken);

        try
        {
            await ProbeCurrentModelAsync(cancellationToken);
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.UndefinedTable ||
            exception.SqlState == PostgresErrorCodes.UndefinedColumn)
        {
            if (!configuration.GetValue("Demo:RecreateOnModelMismatch", false))
            {
                throw new InvalidOperationException(
                    "SF-DEMO-SCHEMA-001: The demo database schema is older than the current application model. " +
                    "Set Demo:RecreateOnModelMismatch=true for the disposable demo database, or migrate the database before starting.",
                    exception);
            }

            logger.LogWarning(
                exception,
                "SF-DEMO-SCHEMA-RECREATE: Demo schema is stale. Recreating the disposable Request Portal demo database.");

            await dbContext.Database.EnsureDeletedAsync(cancellationToken);
            await dbContext.Database.EnsureCreatedAsync(cancellationToken);
            await ProbeCurrentModelAsync(cancellationToken);
        }
    }

    private async Task ProbeCurrentModelAsync(CancellationToken cancellationToken)
    {
        await dbContext.AssistanceRequests
            .AsNoTracking()
            .Select(item => new { item.Id, item.Kind, item.Priority, item.Status })
            .Take(1)
            .ToListAsync(cancellationToken);

        await dbContext.ProjectProposals.AsNoTracking().Take(1).ToListAsync(cancellationToken);
        await dbContext.ProjectBudgetItems.AsNoTracking().Take(1).ToListAsync(cancellationToken);
        await dbContext.ProjectMilestones.AsNoTracking().Take(1).ToListAsync(cancellationToken);
        await dbContext.RequestCategories.AsNoTracking().Take(1).ToListAsync(cancellationToken);
        await dbContext.ProgramClassifications.AsNoTracking().Take(1).ToListAsync(cancellationToken);
        await dbContext.UnitsOfMeasure.AsNoTracking().Take(1).ToListAsync(cancellationToken);
        await dbContext.NotificationPreferences.AsNoTracking().Take(1).ToListAsync(cancellationToken);
        await dbContext.NotificationHistory.AsNoTracking().Take(1).ToListAsync(cancellationToken);
        await dbContext.InboxMessages.AsNoTracking().Take(1).ToListAsync(cancellationToken);
        await dbContext.OutboxMessages.AsNoTracking().Take(1).ToListAsync(cancellationToken);
    }
}
