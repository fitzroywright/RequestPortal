using RequestPortal.Common.Security;
using System.Data.Common;
using System.Security.Claims;
using Common.Security.Abstractions;
using Common.Security.Authorization;
using Common.Security.Models;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Infrastructure.Persistence;
using RequestPortal.Infrastructure.Security;

namespace RequestPortal.Web.Security;

public sealed record StaffEntry(SecurityAccount Account, string? Email, DateTimeOffset Registered, DateTimeOffset? LastSignIn,
    IReadOnlyCollection<string> Roles, IReadOnlyList<LinkedAuthenticationIdentity> Identities);
public sealed record StaffAudit(string Actor, string Action, string Time, string? Subject = null, string[]? PreviousRoles = null, string[]? NewRoles = null, string? Detail = null);

/// <summary>Application-owned role persistence through the Common.Security atomic authorization boundary.</summary>
public sealed class StaffAdministrationService(RequestPortalAccounts accounts, RequestPortalDbContext db, IAuthorizationStore roles, IPermissionAuthorizer authorizer)
{
    public async Task RequireAdministratorAsync(ClaimsPrincipal actor)
    {
        string? id = actor.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actor.Identity?.IsAuthenticated != true || !actor.HasClaim(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue)
            || id is null || accounts.Staff.Find(id) is not { Status: SecurityAccountStatus.Active, Population: AccountPopulation.Staff }
            || !(await authorizer.AuthorizeAsync(AuthorizationSubject.Create(id, actor.FindAll("directory_group").Select(x => x.Value)), RequestPortalPermissions.ManageSecurity)).Allowed)
            throw new UnauthorizedAccessException("Active staff security administration permission is required.");
    }

    public async Task<IReadOnlyList<StaffEntry>> DirectoryAsync(ClaimsPrincipal actor)
    {
        await RequireAdministratorAsync(actor);
        var users = await db.Users.AsNoTracking().Where(x => x.IsInternalStaff).ToDictionaryAsync(x => x.Id.ToString());
        var result = new List<StaffEntry>();
        foreach (var account in accounts.Staff.List().Where(x => x.Population == AccountPopulation.Staff))
        {
            if (!users.TryGetValue(account.Id, out var user)) continue;
            result.Add(new(account, user.Email, user.CreatedUtc, user.LastSignInUtc,
                await roles.GetDirectRoleKeysAsync(account.Id), accounts.Staff.GetIdentities(account.Id)));
        }
        return result;
    }

    public async Task<IReadOnlyCollection<RoleDefinition>> RolesAsync(ClaimsPrincipal actor)
    {
        await RequireAdministratorAsync(actor);
        return await RoleCatalogAsync();
    }

    private async Task<IReadOnlyCollection<RoleDefinition>> RoleCatalogAsync()
    {
        // Initializes the existing Common.Security application authorization adapter.
        await roles.GetDirectRoleKeysAsync(Guid.Empty.ToString());
        return await roles.GetRolesAsync(new[] { PortalRoles.Administrator, PortalRoles.Reviewer });
    }

    public async Task<IReadOnlyCollection<string>> PermissionsAsync(ClaimsPrincipal actor, string target)
    {
        await RequireAdministratorAsync(actor);
        var account = RequireStaff(target);
        if (account.Status != SecurityAccountStatus.Active) return [];
        var catalog = await roles.GetRolesAsync(await db.Roles.Where(x=>x.Name!=null).Select(x=>x.Name!).ToArrayAsync());
        var groups = await ObservedGroupsAsync(target);
        var effective = new List<string>();
        foreach (string permission in catalog.SelectMany(x => x.Permissions).Where(x => x != "*")
                     .Append(RequestPortalPermissions.Access).Append(RequestPortalPermissions.ManageSecurity)
                     .Append(RequestPortalPermissions.ReviewRequests).Append(RequestPortalPermissions.ViewManagementDashboard)
                     .Append(RequestPortalPermissions.IdentityView).Append(RequestPortalPermissions.IdentityRecoveryInitiate).Append(RequestPortalPermissions.IdentityRecoveryApprove).Distinct())
            if ((await authorizer.AuthorizeAsync(AuthorizationSubject.Create(target, groups), permission)).Allowed) effective.Add(permission);
        return effective;
    }

    private async Task<string[]> ObservedGroupsAsync(string target)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT groups FROM requestportal_staff_signins WHERE account_id=@id";
        Add(command,"id",target);
        return (await command.ExecuteScalarAsync()) as string[] ?? [];
    }

    public async Task<IReadOnlyList<StaffAudit>> HistoryAsync(ClaimsPrincipal actor, string target)
    {
        await RequireAdministratorAsync(actor);
        RequireStaff(target);
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        // Explicit, non-keyword alias also supports PostgreSQL 13's stricter alias grammar.
        cmd.CommandText = """
            SELECT coalesce(nullif(u."DisplayName",''),nullif(u."DirectoryUserName",''),nullif(u."UserName",''),events.actor_id),
                events.action,events.audit_time,events.previous_roles::text,events.new_roles::text,events.detail
            FROM (
                SELECT actor_id,action,occurred_at::text AS audit_time,previous_roles,new_roles,detail FROM requestportal_staff_admin_events WHERE subject_id=@id
                UNION ALL
                SELECT actor_id,action,occurred_at,NULL::jsonb,NULL::jsonb,NULL::text FROM common_security_account_events old
                WHERE old.application_id='RequestPortal.Staff' AND old.account_id=@id
                  AND NOT EXISTS(SELECT 1 FROM requestportal_staff_admin_events current WHERE current.id::text=old.event_id)
            ) events LEFT JOIN "AspNetUsers" u ON u."Id"::text=events.actor_id
            ORDER BY events.audit_time::timestamptz DESC LIMIT 200
            """;
        Add(cmd, "id", target);
        var result = new List<StaffAudit>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(new(reader.GetString(0)=="system:role-consolidation"?"System migration":reader.GetString(0),reader.GetString(1),reader.GetString(2),target,
            reader.IsDBNull(3)?null:System.Text.Json.JsonSerializer.Deserialize<string[]>(reader.GetString(3)),
            reader.IsDBNull(4)?null:System.Text.Json.JsonSerializer.Deserialize<string[]>(reader.GetString(4)),reader.IsDBNull(5)?null:reader.GetString(5)));
        return result;
    }

    public async Task<bool> HasAdministratorAsync()
    {
        await roles.GetDirectRoleKeysAsync(Guid.Empty.ToString());
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = AdminCountSql;
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }

    public async Task<bool> IsProvisionedAsync()
    {
        await roles.GetDirectRoleKeysAsync(Guid.Empty.ToString());
        return await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM requestportal_staff_provisioning").SingleAsync() > 0;
    }

    public async Task BootstrapAsync(string target, string provider, string scope, string subject, string authority, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authority);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var targetAccount = RequireStaff(target);
        if (targetAccount.ProviderKind is AuthenticationProviderKind.Mock or AuthenticationProviderKind.LocalDatabase)
            throw new InvalidOperationException("Bootstrap requires an approved external staff identity.");
        if (!accounts.Staff.GetIdentities(target).Any(x => x.Key.ProviderId == provider && x.Key.ScopeId == scope && x.Key.SubjectId == subject))
            throw new InvalidOperationException("The explicitly approved immutable provider identity does not match the existing staff account.");
        await StaffRoleConsolidation.ApplyAsync(db);
        if (await IsProvisionedAsync())
        {
            var recorded = await db.Database.SqlQueryRaw<string>("SELECT account_id AS \"Value\" FROM requestportal_staff_provisioning WHERE singleton=1").SingleAsync();
            if (recorded != target) throw new InvalidOperationException("Initial provisioning is complete for another account.");
            if (accounts.Staff.Find(target)?.Status != SecurityAccountStatus.Active ||
                !(await authorizer.AuthorizeAsync(AuthorizationSubject.Create(target),RequestPortalPermissions.ManageSecurity)).Allowed)
                throw new InvalidOperationException("Provisioning was already completed. Current lifecycle decisions were preserved; use the normal administrative workflow.");
            return;
        }
        var catalog = await RoleCatalogAsync();
        var admin = catalog.Single(x => x.Key == PortalRoles.Administrator);
        if (!admin.Permissions.Contains(PermissionAuthorizer.AllPermissions)) throw new InvalidOperationException("Administrator permissions are incomplete.");
        Mutate(target, "offline:" + authority, "Bootstrap", reason, SecurityAccountStatus.Active, [admin.Key], null, true, null, expectedIdentity: new ExternalIdentityKey(provider,scope,subject));
        if (accounts.Staff.Find(target)?.Status != SecurityAccountStatus.Active ||
            !(await authorizer.AuthorizeAsync(AuthorizationSubject.Create(target), RequestPortalPermissions.ManageSecurity)).Allowed)
            throw new InvalidOperationException("Provisioning persistence verification failed.");
    }

    public async Task ChangeAsync(ClaimsPrincipal actor, string target, string action, string[] selectedRoles, long expectedVersion, bool confirmed = false, string? displayName = null, string? email = null)
    {
        await RequireAdministratorAsync(actor);
        if (action is "Suspend" or "Revoke" or "Reject" && !confirmed)
            throw new InvalidOperationException("Confirm suspension or revocation before continuing.");
        RequireStaff(target);
        if (displayName is not null && (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 200))
            throw new InvalidOperationException("Staff name must contain 1–200 characters.");
        if (!string.IsNullOrEmpty(email) && (!System.Net.Mail.MailAddress.TryCreate(email, out var address) || address.Address != email || email.Length > 256))
            throw new InvalidOperationException("Enter a valid contact email address.");
        var catalog = await RoleCatalogAsync();
        if (selectedRoles.Any(x => !catalog.Any(r => r.Key == x))) throw new InvalidOperationException("Unknown role.");
        SecurityAccountStatus status = action switch {
            "Approve" or "Activate" => SecurityAccountStatus.Active,
            "Suspend" => SecurityAccountStatus.Suspended,
            "Reject" or "Revoke" => SecurityAccountStatus.Disabled,
            "Save" => accounts.Staff.Find(target)!.Status,
            _ => throw new InvalidOperationException("Unknown administrative action.") };
        if (action is "Reject" or "Revoke") selectedRoles = [];
        if (status == SecurityAccountStatus.Active && selectedRoles.Length == 0) throw new InvalidOperationException("Active staff require at least one role.");
        var actorId = actor.FindFirstValue(ClaimTypes.NameIdentifier)!;
        if (actorId == target)
        {
            var subject=AuthorizationSubject.Create(actorId,actor.FindAll("directory_group").Select(x=>x.Value));
            foreach (var permission in catalog.Where(x=>selectedRoles.Contains(x.Key)).SelectMany(x=>x.Permissions).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!(await authorizer.AuthorizeAsync(subject,permission)).Allowed)
                    throw new UnauthorizedAccessException("You cannot grant yourself additional permissions.");
        }
        var actorVersion = accounts.Staff.Find(actorId)!.SessionVersion;
        // Repeat authorization after capturing the version so a concurrent revocation cannot pass unnoticed.
        await RequireAdministratorAsync(actor);
        Mutate(target, actorId, action, "Staff access updated", status, selectedRoles.Distinct().ToArray(), expectedVersion, false, actorVersion, displayName, email);
    }

    private SecurityAccount RequireStaff(string id)
    {
        var account = accounts.Staff.Find(id);
        if (account?.Population != AccountPopulation.Staff || !db.Users.AsNoTracking().Any(x => x.Id == Guid.Parse(id) && x.IsInternalStaff))
            throw new InvalidOperationException("An existing verified staff account is required.");
        return account;
    }

    private void Mutate(string target, string actor, string action, string reason, SecurityAccountStatus status, string[] selected, long? version, bool bootstrap, long? actorVersion, string? displayName = null, string? email = null, ExternalIdentityKey? expectedIdentity = null)
    {
        if (bootstrap && string.IsNullOrWhiteSpace(reason) || reason.Length > 1000) throw new InvalidOperationException("A decision reason of 1–1000 characters is required.");
        accounts.Staff.ChangeAuthorization(target, actor, (connection, transaction) =>
        {
            // Table lock serializes last-administrator decisions, including concurrent requests.
            Execute(connection, transaction, "LOCK TABLE common_security_accounts IN SHARE ROW EXCLUSIVE MODE");
            if (bootstrap)
            {
                if (expectedIdentity is null || Convert.ToInt32(Scalar(connection, transaction,
                    "SELECT count(*) FROM common_security_identities WHERE application_id='RequestPortal.Staff' AND account_id=@id AND provider_id=@provider AND scope_id=@scope AND subject_id=@subject",
                    ("id",target),("provider",expectedIdentity.ProviderId),("scope",expectedIdentity.ScopeId),("subject",expectedIdentity.SubjectId))) != 1)
                    throw new InvalidOperationException("The approved staff binding changed before provisioning. Verify the identity again.");
                var prior = Scalar(connection, transaction, "SELECT account_id FROM requestportal_staff_provisioning WHERE singleton=1");
                if (prior is not null) {
                    if ((string)prior != target) throw new InvalidOperationException("Initial provisioning is complete for another account.");
                    // Never undo later suspensions or role revocations on a repeat invocation.
                    return;
                }
                if (Convert.ToInt32(Scalar(connection, transaction, AdminCountSql)) > 0)
                    throw new InvalidOperationException("An active administrator already exists. Use normal staff administration.");
            }
            else
            {
                var actorStatus = Scalar(connection, transaction, "SELECT status FROM common_security_accounts WHERE application_id='RequestPortal.Staff' AND account_id=@id", ("id", actor));
                if (actorStatus is null || Convert.ToInt32(actorStatus) != (int)SecurityAccountStatus.Active ||
                    Convert.ToInt64(Scalar(connection, transaction, "SELECT session_version FROM common_security_accounts WHERE application_id='RequestPortal.Staff' AND account_id=@id", ("id", actor))) != actorVersion)
                    throw new UnauthorizedAccessException();
                if (Convert.ToInt64(Scalar(connection, transaction, "SELECT session_version FROM common_security_accounts WHERE application_id='RequestPortal.Staff' AND account_id=@id", ("id", target))) != version)
                    throw new InvalidOperationException("This account changed. Reload before saving.");
            }
            if (status != SecurityAccountStatus.Active || !selected.Contains(PortalRoles.Administrator))
            {
                var isAdmin = Convert.ToInt32(Scalar(connection, transaction, AdminCountSql + " AND a.account_id=@id", ("id", target))) > 0;
                if (isAdmin && Convert.ToInt32(Scalar(connection, transaction, AdminCountSql)) <= 1)
                    throw new InvalidOperationException("The last active administrator cannot be suspended, revoked or lose the Administrator role.");
            }
            int previousStatus=Convert.ToInt32(Scalar(connection,transaction,"SELECT status FROM common_security_accounts WHERE application_id='RequestPortal.Staff' AND account_id=@id",("id",target)));
            var previousRoles = new List<string>();
            using (var command = connection.CreateCommand()) {
                command.Transaction=transaction;
                command.CommandText="SELECT r.\"Name\" FROM \"AspNetUserRoles\" ur JOIN \"AspNetRoles\" r ON r.\"Id\"=ur.\"RoleId\" WHERE ur.\"UserId\"=@id ORDER BY r.\"Name\"";
                Add(command,"id",Guid.Parse(target));
                using var reader=command.ExecuteReader(); while(reader.Read()) previousRoles.Add(reader.GetString(0));
            }
            if (displayName is not null) {
                Execute(connection,transaction,"UPDATE common_security_accounts SET display_name=@name WHERE application_id='RequestPortal.Staff' AND account_id=@id",("name",displayName.Trim()),("id",target));
                Execute(connection,transaction,"UPDATE \"AspNetUsers\" SET \"DisplayName\"=@name, \"Email\"=@email WHERE \"Id\"=@id AND \"IsInternalStaff\"",("name",displayName.Trim()),("email",(object?)email ?? DBNull.Value),("id",Guid.Parse(target)));
            }
            Execute(connection, transaction, "DELETE FROM \"AspNetUserRoles\" WHERE \"UserId\"=@id AND \"RoleId\" IN(SELECT \"Id\" FROM \"AspNetRoles\" WHERE \"Name\" IN ('Administrator','Reviewer'))", ("id", Guid.Parse(target)));
            foreach (var role in selected.Distinct())
                Execute(connection, transaction, "INSERT INTO \"AspNetUserRoles\" (\"UserId\",\"RoleId\") SELECT @id,\"Id\" FROM \"AspNetRoles\" WHERE \"Name\"=@role ON CONFLICT DO NOTHING", ("id", Guid.Parse(target)), ("role", role));
            Execute(connection, transaction, "UPDATE common_security_accounts SET status=@status WHERE application_id='RequestPortal.Staff' AND account_id=@id", ("status", (int)status), ("id", target));
            string eventId=Guid.NewGuid().ToString();
            Execute(connection, transaction, "INSERT INTO common_security_account_events(application_id,event_id,account_id,actor_id,action,occurred_at) VALUES('RequestPortal.Staff',@event,@id,@actor,@action,@time)",
                ("event", eventId), ("id", target), ("actor", actor), ("action", action + ": " + reason + "; previous roles=" + string.Join(",",previousRoles) + "; roles=" + string.Join(",", selected) + (displayName is null ? "" : "; profile updated")), ("time", DateTimeOffset.UtcNow.ToString("O")));
            Execute(connection,transaction,"""
                INSERT INTO requestportal_staff_admin_events(id,subject_id,actor_id,action,previous_roles,new_roles,previous_status,new_status,detail)
                VALUES(@event,@id,@actor,@action,@previous::jsonb,
                    (SELECT coalesce(jsonb_agg(r."Name" ORDER BY r."Name"),'[]'::jsonb) FROM "AspNetUserRoles" ur JOIN "AspNetRoles" r ON r."Id"=ur."RoleId" WHERE ur."UserId"=@user),
                    @oldStatus,@newStatus,@detail)
                """,("event",Guid.Parse(eventId)),("id",target),("actor",actor),("action",action),
                ("previous",System.Text.Json.JsonSerializer.Serialize(previousRoles)),("user",Guid.Parse(target)),
                ("oldStatus",previousStatus),("newStatus",(int)status),("detail",bootstrap?reason:""));
            if (bootstrap) Execute(connection, transaction, "INSERT INTO requestportal_staff_provisioning(singleton,account_id,actor_id,completed_at) VALUES(1,@id,@actor,@time)", ("id", target), ("actor", actor), ("time", DateTimeOffset.UtcNow));
        });
    }

    private const string AdminCountSql = """
        SELECT count(*) FROM common_security_accounts a
        JOIN "AspNetUserRoles" ur ON ur."UserId"::text=a.account_id
        JOIN "AspNetRoles" r ON r."Id"=ur."RoleId"
        WHERE a.application_id='RequestPortal.Staff' AND a.population=0 AND a.status=1 AND r."Name"='Administrator'
        """;
    private static void Add(DbCommand cmd, string name, object value) { var p = cmd.CreateParameter(); p.ParameterName=name; p.Value=value; cmd.Parameters.Add(p); }
    private static object? Scalar(DbConnection c, DbTransaction t, string sql, params (string Name, object Value)[] parameters) {
        using var cmd=c.CreateCommand(); cmd.Transaction=t; cmd.CommandText=sql; foreach(var p in parameters) Add(cmd,p.Name,p.Value); return cmd.ExecuteScalar(); }
    private static void Execute(DbConnection c, DbTransaction t, string sql, params (string Name, object Value)[] parameters) {
        using var cmd=c.CreateCommand(); cmd.Transaction=t; cmd.CommandText=sql; foreach(var p in parameters) Add(cmd,p.Name,p.Value); cmd.ExecuteNonQuery(); }
}
