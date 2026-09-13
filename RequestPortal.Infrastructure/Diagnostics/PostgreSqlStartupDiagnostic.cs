using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Common.Diagnostics;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Diagnostics;

public sealed class PostgreSqlStartupDiagnostic : IStartupDiagnostic
{
    private readonly RequestPortalDbContext dbContext;

    public PostgreSqlStartupDiagnostic(RequestPortalDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public string Name => "PostgreSQL";

    public async Task<DiagnosticCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            bool canConnect = await dbContext.Database.CanConnectAsync(cancellationToken);
            return canConnect
                ? new DiagnosticCheckResult(Name, true, "Database connection succeeded.")
                : new DiagnosticCheckResult(Name, false, "Database connection failed.");
        }
        catch (Exception exception)
        {
            return new DiagnosticCheckResult(Name, false, exception.Message);
        }
    }
}
