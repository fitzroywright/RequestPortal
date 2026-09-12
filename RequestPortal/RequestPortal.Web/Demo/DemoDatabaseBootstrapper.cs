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

        if (!await dbContext.Database.CanConnectAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "RP-DEMO-DB-001: The Request Portal database does not exist or is not reachable. " +
                "Provision the PostgreSQL database once with an administrative account, grant the application account access, " +
                "then restart Request Portal. The application account is intentionally not required to have CREATEDB permission.");
        }

        await dbContext.Database.MigrateAsync(cancellationToken);

        try
        {
            await ProbeCurrentModelAsync(cancellationToken);
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.UndefinedTable ||
            exception.SqlState == PostgresErrorCodes.UndefinedColumn)
        {
            logger.LogError(
                exception,
                "RP-DEMO-SCHEMA-001: Demo schema does not match the current Request Portal model.");

            throw new InvalidOperationException(
                "RP-DEMO-SCHEMA-001: The demo database schema does not match the current application model. " +
                "Apply the current EF Core migrations or recreate the disposable demo database with an administrative PostgreSQL account. " +
                "Request Portal will not drop or recreate databases using the application account.",
                exception);
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
