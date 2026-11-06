using Microsoft.EntityFrameworkCore;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Management;

/// <summary>Operator-only aggregate counts and catalog checks. Never used by dashboard HTTP requests.</summary>
public static class ManagementDashboardSourceVerification
{
    public sealed record Evidence(int Requests, int Outstanding, int Completed, bool Reconciled,
        string SchemaOwner, bool MigrationPrivileges, int DashboardTriggers, bool TriggerFunctionPresent);

    public static async Task<Evidence> ReadAsync(RequestPortalDbContext db,
        RequestPortal.Application.Management.ManagementDashboardSnapshot? snapshot, CancellationToken ct)
    {
        var source = from request in db.AssistanceRequests.AsNoTracking()
                     join organization in db.Organizations.AsNoTracking() on request.OrganizationId equals organization.Id
                     where request.Status != RequestStatus.Draft && request.Status != RequestStatus.Cancelled && request.Status != RequestStatus.Duplicate
                     select request;
        int requests = await source.CountAsync(ct);
        int outstanding = await source.CountAsync(x => x.Status == RequestStatus.Submitted || x.Status == RequestStatus.MoreInformationRequired ||
            x.Status == RequestStatus.Approved || x.Status == RequestStatus.PendingFulfilment, ct);
        int completed = await source.CountAsync(x => x.Status == RequestStatus.Fulfilled || x.Status == RequestStatus.Closed, ct);
        string owner = await db.Database.SqlQueryRaw<string>("SELECT pg_get_userbyid(nspowner) AS \"Value\" FROM pg_namespace WHERE nspname=current_schema()").SingleAsync(ct);
        bool privileges = await db.Database.SqlQueryRaw<bool>("""
            SELECT has_schema_privilege(current_schema(),'CREATE') AND has_language_privilege('plpgsql','USAGE')
              AND has_table_privilege('outbox_messages','INSERT')
              AND (SELECT COALESCE(bool_and(pg_has_role(c.relowner,'USAGE') AND has_table_privilege(c.oid,'TRIGGER')),false)
                   FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                   WHERE n.nspname=current_schema() AND c.relname IN
                     ('assistance_requests','request_status_history','quick_intakes','documents','deliveries',
                      'delivery_returns','organizations','request_categories','program_classifications','integration_links')) AS "Value"
            """).SingleAsync(ct);
        int triggers = await db.Database.SqlQueryRaw<int>("""
            SELECT count(*)::int AS "Value" FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid
            JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname=current_schema() AND NOT t.tgisinternal AND t.tgenabled<>'D'
              AND t.tgname='requestportal_dashboard_refresh'
            """).SingleAsync(ct);
        bool function = await db.Database.SqlQueryRaw<bool>("""
            SELECT EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
              WHERE n.nspname=current_schema() AND p.proname='requestportal_queue_dashboard_refresh') AS "Value"
            """).SingleAsync(ct);
        return new(requests, outstanding, completed, snapshot is not null && requests == snapshot.Statuses.Sum(x => x.Count) &&
            outstanding == snapshot.Executive.Outstanding && completed == snapshot.Executive.Completed,
            owner, privileges, triggers, function);
    }
}
