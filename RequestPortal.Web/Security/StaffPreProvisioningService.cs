using System.Data.Common;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Common.Security.Abstractions;
using Common.Security.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RequestPortal.Common.Security;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Web.Security;

public sealed record StaffDirectoryChoice(DirectoryStaffIdentity Identity, string Selection);
public sealed record StaffProvisioningResult(string AccountId, bool Created);

/// <summary>Creates a bound staff account, never a sign-in session. All browser-supplied identity fields are untrusted.</summary>
public sealed class StaffPreProvisioningService(StaffAdministrationService administration, RequestPortalAccounts accounts,
    RequestPortalDbContext db, IStaffDirectoryLookup directory, IDataProtectionProvider protection, TimeProvider clock)
{
    private sealed record Selection(string Actor, long ActorVersion, string Configuration, DirectoryStaffIdentity Identity, DateTimeOffset Expires);
    private readonly IDataProtector protector = protection.CreateProtector("RequestPortal.StaffDirectory.Selection.v1");
    public string? UnavailableReason => directory.UnavailableReason;

    public async Task<IReadOnlyList<StaffDirectoryChoice>> SearchAsync(ClaimsPrincipal actor, string query, CancellationToken token)
    {
        await administration.RequireAdministratorAsync(actor);
        string actorId = actor.FindFirstValue(ClaimTypes.NameIdentifier)!;
        long version = accounts.Staff.Find(actorId)!.SessionVersion;
        var matches = await directory.SearchAsync(query,token);
        return matches.Select(identity => new StaffDirectoryChoice(identity,protector.Protect(JsonSerializer.Serialize(
            new Selection(actorId,version,directory.ConfigurationKey,identity,clock.GetUtcNow().AddMinutes(10)))))).ToArray();
    }

    public async Task<DirectoryStaffIdentity> PreviewAsync(ClaimsPrincipal actor, string selection, CancellationToken token)
    {
        return (await VerifyAsync(actor,selection,token)).Identity;
    }

    private async Task<(Selection Selection, DirectoryStaffIdentity Identity)> VerifyAsync(ClaimsPrincipal actor, string selection, CancellationToken token)
    {
        await administration.RequireAdministratorAsync(actor);
        if (directory.UnavailableReason is { } reason) throw new InvalidOperationException(reason);
        Selection approved;
        try {
            if (string.IsNullOrWhiteSpace(selection) || selection.Length > 16000) throw new CryptographicException();
            approved = JsonSerializer.Deserialize<Selection>(protector.Unprotect(selection)) ?? throw new CryptographicException();
        }
        catch (Exception e) when (e is CryptographicException or JsonException or ArgumentException) {
            throw new InvalidOperationException("The directory selection is invalid. Search again.");
        }
        var actorId = actor.FindFirstValue(ClaimTypes.NameIdentifier)!;
        if (approved.Actor != actorId || approved.ActorVersion != accounts.Staff.Find(actorId)!.SessionVersion ||
            approved.Configuration != directory.ConfigurationKey || approved.Expires <= clock.GetUtcNow())
            throw new InvalidOperationException("The directory selection has expired or access has changed. Search again.");
        // Re-read authoritative provider data before creating anything. A signed search result alone is insufficient.
        var current = await directory.FindAsync(approved.Identity.Id,token);
        if (current is null || current.Id != approved.Identity.Id || current.Key != approved.Identity.Key || current.Kind != approved.Identity.Kind)
            throw new InvalidOperationException("The selected directory identity changed or no longer exists. Search again.");
        if (!current.Enabled) throw new InvalidOperationException("This directory account is disabled and cannot be provisioned.");
        if (current.Kind is not (AuthenticationProviderKind.EntraId or AuthenticationProviderKind.External))
            throw new InvalidOperationException("Only the configured staff directory identity provider is supported.");
        await administration.RequireAdministratorAsync(actor);
        return (approved,current);
    }

    public async Task<StaffProvisioningResult> ProvisionAsync(ClaimsPrincipal actor, string selection, string[] selectedRoles, bool activate, CancellationToken token)
    {
        var (approved,identity) = await VerifyAsync(actor,selection,token);
        var catalog = await administration.RolesAsync(actor);
        if (selectedRoles.Length==0 || selectedRoles.Any(x => (x!=PortalRoles.Administrator && x!=PortalRoles.Reviewer) || !catalog.Any(r=>r.Key==x)))
            throw new InvalidOperationException("Select Administrator or Reviewer.");
        selectedRoles = selectedRoles.Distinct().Order().ToArray();
        // Idempotent repeats never overwrite an existing account's roles, lifecycle, profile or bindings.
        if (await ExistingAsync(identity.Key,token) is { } existing) return new(existing,false);
        var creation = new AccountCreation(Guid.NewGuid().ToString("D"),identity.UserName,identity.DisplayName,identity.Kind,
            AccountPopulation.Staff,activate ? SecurityAccountStatus.Active : SecurityAccountStatus.PendingAccess,identity.Key);
        try {
            var created = accounts.CreateStaff(creation,approved.Actor,(connection,transaction,account)=> {
                // Hold the actor row until commit so concurrent suspension/role changes cannot authorize stale provisioning.
                using var guard = Command(connection,transaction,"SELECT status,session_version,population FROM common_security_accounts WHERE application_id='RequestPortal.Staff' AND account_id=@actor FOR UPDATE",("actor",approved.Actor));
                using (var reader=guard.ExecuteReader()) {
                    if (!reader.Read() || reader.GetInt32(0)!=(int)SecurityAccountStatus.Active || reader.GetInt64(1)!=approved.ActorVersion || reader.GetInt32(2)!=(int)AccountPopulation.Staff)
                        throw new UnauthorizedAccessException("Administrative access changed. Sign in again.");
                }
                using (var revoked=Command(connection,transaction,"SELECT count(*) FROM common_security_revoked_identities WHERE application_id='RequestPortal.Staff' AND provider_id=@provider AND scope_id=@scope AND subject_id=@subject",
                    ("provider",identity.Key.ProviderId),("scope",identity.Key.ScopeId),("subject",identity.Key.SubjectId)))
                    if (Convert.ToInt32(revoked.ExecuteScalar())!=0) throw new InvalidOperationException("This identity was revoked. Use the existing identity recovery workflow.");
                foreach (var role in selectedRoles) {
                    using var assignment=Command(connection,transaction,"INSERT INTO \"AspNetUserRoles\" (\"UserId\",\"RoleId\") SELECT @id,\"Id\" FROM \"AspNetRoles\" WHERE \"Name\"=@role ON CONFLICT DO NOTHING",("id",Guid.Parse(account.Id)),("role",role));
                    if (assignment.ExecuteNonQuery()!=1) throw new InvalidOperationException("The selected role changed. Reload before provisioning.");
                }
                using (var contact=Command(connection,transaction,"UPDATE \"AspNetUsers\" SET \"Email\"=@email WHERE \"Id\"=@id AND \"IsInternalStaff\"",("email",(object?)identity.Email ?? DBNull.Value),("id",Guid.Parse(account.Id)))) contact.ExecuteNonQuery();
                using var audit=Command(connection,transaction,"""
                    INSERT INTO requestportal_staff_admin_events(id,subject_id,actor_id,action,occurred_at,previous_roles,new_roles,previous_status,new_status,detail)
                    VALUES(@event,@id,@actor,'PreProvisioned',@time,'[]'::jsonb,@roles::jsonb,NULL,@status,@detail)
                    """,("event",Guid.NewGuid()),("id",account.Id),("actor",approved.Actor),("time",clock.GetUtcNow()),("roles",JsonSerializer.Serialize(selectedRoles)),
                    ("status",(int)account.Status),("detail",$"Verified directory identity: {identity.Key.ProviderId} / {identity.Key.ScopeId} / {identity.Key.SubjectId}. " + (activate?"Access approved before first sign-in.":"Awaiting access approval.")));
                audit.ExecuteNonQuery();
            });
            return new(created.Id,true);
        }
        catch (Exception e) when (e is IdentityAlreadyLinkedException or IdentityEnrollmentRequiredException || e is PostgresException { SqlState: "23505" }) {
            if (await ExistingAsync(identity.Key,token) is { } raced) return new(raced,false);
            throw new InvalidOperationException("This username already belongs to another identity. No account was changed; review the existing binding.");
        }
    }

    private async Task<string?> ExistingAsync(ExternalIdentityKey key,CancellationToken token)
    {
        var id = await db.Database.SqlQueryRaw<string>("""
            SELECT account_id AS "Value" FROM common_security_identities
            WHERE application_id='RequestPortal.Staff' AND provider_id={0} AND scope_id={1} AND subject_id={2}
            """,key.ProviderId,key.ScopeId,key.SubjectId).SingleOrDefaultAsync(token);
        if (id is not null && (accounts.Staff.Find(id)?.Population != AccountPopulation.Staff || !await db.Users.AsNoTracking().AnyAsync(x=>x.Id==Guid.Parse(id)&&x.IsInternalStaff,token)))
            throw new InvalidOperationException("This identity has an incompatible existing account. No account was changed.");
        return id;
    }

    private static DbCommand Command(DbConnection connection, DbTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        var command=connection.CreateCommand(); command.Transaction=transaction; command.CommandText=sql;
        foreach (var (name,value) in parameters) { var parameter=command.CreateParameter(); parameter.ParameterName=name; parameter.Value=value; command.Parameters.Add(parameter); }
        return command;
    }
}
