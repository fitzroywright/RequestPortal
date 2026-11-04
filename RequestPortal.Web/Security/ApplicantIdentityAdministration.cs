using Microsoft.EntityFrameworkCore.Storage;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Security.Abstractions;
using Common.Security.Authorization;
using Common.Security.Hosting;
using Common.Security.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RequestPortal.Common.Messaging;
using ApplicationUser = RequestPortal.Infrastructure.Identity.ApplicationUser;
using RequestPortal.Infrastructure.Persistence;
using RequestPortal.Infrastructure.Security;

namespace RequestPortal.Web.Security;

public sealed record ApplicantAccount(Guid Id, string Name, string Email, string Status, DateTimeOffset Registered, DateTimeOffset? LastSignIn, string[] Providers);
public sealed record ApplicantAccounts(IReadOnlyList<ApplicantAccount> Items, int Total, int Page, int PageSize);
public sealed record ApplicantRelationship(Guid Id, string Label, string Status);
public sealed record ApplicantRecovery(Guid Id, string Provider, string Status, Guid InitiatedBy, Guid? ApprovedBy, string Reason, string? Verification, DateTimeOffset Created, DateTimeOffset Expires);
public sealed record ApplicantRecoveryEvent(string Actor, string Action, string Detail, DateTimeOffset Time);
public sealed record ApplicantAccountDetails(ApplicantAccount Account, int RequestCount, IReadOnlyList<ApplicantRelationship> Requests,
    IReadOnlyList<ApplicantRelationship> Memberships, IReadOnlyList<ApplicantRecovery> Recoveries, IReadOnlyList<ApplicantRecoveryEvent> Events);
public sealed record ApplicantRecoveryInvitation(Guid RecoveryId, Guid ApplicantId, string Provider);
public sealed class FreshAdministratorAuthenticationRequiredException : InvalidOperationException;

/// <summary>Administrative recovery adds verified providers only. Common.Security owns account IDs, immutable binding uniqueness and session invalidation.</summary>
public sealed class ApplicantIdentityAdministration(RequestPortalAccounts accounts, RequestPortalDbContext db,
    IPermissionAuthorizer authorizer, IAuthenticationSchemeProvider schemes, TimeProvider time)
{
    public static readonly string[] SupportedProviders = ["Google", "Microsoft", "Yahoo", "Apple", "Facebook"];
    private DateTimeOffset Now => time.GetUtcNow();
    public async Task<string> RequireAsync(ClaimsPrincipal actor, string permission, bool fresh = false)
    {
        string? id = actor.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actor.Identity?.IsAuthenticated != true || !actor.HasClaim(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue)
            || !Guid.TryParse(id, out _) || accounts.Staff.Find(id) is not { Status: SecurityAccountStatus.Active, Population: AccountPopulation.Staff })
            throw new UnauthorizedAccessException("Active staff administration access is required.");
        var subject = AuthorizationSubject.Create(id, actor.FindAll("directory_group").Select(c => c.Value));
        if (!(await authorizer.AuthorizeAsync(subject, RequestPortalPermissions.ManageSecurity)).Allowed ||
            !(await authorizer.AuthorizeAsync(subject, permission)).Allowed)
            throw new UnauthorizedAccessException("The applicant identity permission is required.");
        if (fresh && (!long.TryParse(actor.FindFirstValue(AccountSecurityHosting.AuthenticationTimeClaim), out long seconds) ||
            seconds < Now.AddMinutes(-10).ToUnixTimeSeconds() || seconds > Now.AddMinutes(2).ToUnixTimeSeconds()))
            throw new FreshAdministratorAuthenticationRequiredException();
        return id;
    }
    public async Task<string[]> ProvidersAsync() => (await schemes.GetAllSchemesAsync()).Select(s => s.Name).Where(SupportedProviders.Contains).ToArray();
    private async Task<NpgsqlCommand> Command(string sql, params (string Name, object Value)[] parameters)
    {
        await db.Database.OpenConnectionAsync();
        var command = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction());
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return command;
    }
    private const string AccountSelect = """
        SELECT u."Id", coalesce(nullif(trim(coalesce(u."FirstName",'') || ' ' || coalesce(u."LastName",'')),''),nullif(u."DisplayName",''),'Name not provided'),
            coalesce(u."Email",''), CASE a.status WHEN 0 THEN 'PendingAccess' WHEN 1 THEN 'Active' WHEN 2 THEN 'Suspended' WHEN 3 THEN 'Disabled' ELSE 'Enrollment required' END,
            u."CreatedUtc",u."LastSignInUtc",ARRAY(SELECT DISTINCT i.provider_id FROM common_security_identities i
                WHERE i.application_id='RequestPortal.Public' AND i.account_id=u."Id"::text ORDER BY i.provider_id)
        FROM "AspNetUsers" u LEFT JOIN common_security_accounts a ON a.application_id='RequestPortal.Public' AND a.account_id=u."Id"::text
        """;
    private static ApplicantAccount ReadAccount(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4), reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<string[]>(6));
    public async Task<ApplicantAccounts> SearchAsync(ClaimsPrincipal actor, string? query, string? status, int page, CancellationToken ct = default)
    {
        await RequireAsync(actor, RequestPortalPermissions.IdentityView);
        string search = string.Join(" ", (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (search.Length > 200) throw new InvalidOperationException("Search must be at most 200 characters.");
        if (!string.IsNullOrEmpty(status) && !new[] { "PendingAccess", "Active", "Suspended", "Disabled", "Enrollment required" }.Contains(status)) throw new InvalidOperationException("Choose a valid account status.");
        string pattern = "%" + search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        string filter = """
             WHERE NOT u."IsInternalStaff" AND (@status='' OR CASE a.status WHEN 0 THEN 'PendingAccess' WHEN 1 THEN 'Active' WHEN 2 THEN 'Suspended' WHEN 3 THEN 'Disabled' ELSE 'Enrollment required' END=@status)
             AND (@search='' OR concat_ws(' ',u."FirstName",u."LastName") ILIKE @pattern ESCAPE '\' OR u."DisplayName" ILIKE @pattern ESCAPE '\'
                OR u."Email" ILIKE @pattern ESCAPE '\' OR EXISTS(SELECT 1 FROM "assistance_requests" r LEFT JOIN "quick_intakes" q ON q."RequestId"=r."Id"
                    WHERE (coalesce(r."SubmittedByUserId",r."CreatedByUserId")=u."Id" OR q."ConvertedToUserId"=u."Id" OR q."AnonymousUserId"=u."Id") AND r."PublicReference" ILIKE @pattern ESCAPE '\'))
            """;
        await using var count = await Command("SELECT count(*) FROM \"AspNetUsers\" u LEFT JOIN common_security_accounts a ON a.application_id='RequestPortal.Public' AND a.account_id=u.\"Id\"::text" + filter, ("status",status ?? ""),("search",search),("pattern",pattern));
        int total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
        page = Math.Clamp(page, 1, Math.Max(1,(int)Math.Ceiling(total/25d)));
        await using var command = await Command(AccountSelect + filter + " ORDER BY lower(u.\"DisplayName\"),u.\"Id\" LIMIT 25 OFFSET @offset", ("status",status ?? ""),("search",search),("pattern",pattern),("offset",(page-1)*25));
        var rows = new List<ApplicantAccount>(); await using var reader = await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct)) rows.Add(ReadAccount(reader));
        return new(rows,total,page,25);
    }
    public async Task<ApplicantAccountDetails?> DetailsAsync(ClaimsPrincipal actor, Guid id, CancellationToken ct = default)
    {
        await RequireAsync(actor, RequestPortalPermissions.IdentityView);
        ApplicantAccount account;
        await using(var command = await Command(AccountSelect + " WHERE u.\"Id\"=@id AND NOT u.\"IsInternalStaff\"", ("id",id)))
        await using(var reader = await command.ExecuteReaderAsync(ct)) { if(!await reader.ReadAsync(ct)) return null; account=ReadAccount(reader); }
        var requests = await db.AssistanceRequests.AsNoTracking().Where(r => (r.SubmittedByUserId ?? r.CreatedByUserId)==id || db.QuickIntakes.Any(q=>q.RequestId==r.Id && (q.ConvertedToUserId==id || q.AnonymousUserId==id)))
            .OrderByDescending(r=>r.SubmittedUtc ?? r.CreatedUtc).Select(r=>new ApplicantRelationship(r.Id,r.PublicReference,r.Status.ToString())).Take(25).ToListAsync(ct);
        int requestCount=await db.AssistanceRequests.CountAsync(r=>(r.SubmittedByUserId ?? r.CreatedByUserId)==id || db.QuickIntakes.Any(q=>q.RequestId==r.Id && (q.ConvertedToUserId==id || q.AnonymousUserId==id)),ct);
        var memberships=await (from m in db.OrganizationMemberships.AsNoTracking() join o in db.Organizations.AsNoTracking() on m.OrganizationId equals o.Id where m.UserId==id orderby o.Name select new ApplicantRelationship(o.Id,o.Name,m.Status.ToString())).ToListAsync(ct);
        var recoveries=new List<ApplicantRecovery>();
        await using(var command=await Command("SELECT id,provider,status,initiated_by,approved_by,reason,verification_reference,created_utc,expires_utc FROM requestportal_identity_recoveries WHERE applicant_id=@id ORDER BY created_utc DESC LIMIT 25",("id",id)))
        await using(var reader=await command.ExecuteReaderAsync(ct)) while(await reader.ReadAsync(ct)) recoveries.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetGuid(3),reader.IsDBNull(4)?null:reader.GetGuid(4),reader.GetString(5),reader.IsDBNull(6)?null:reader.GetString(6),reader.GetFieldValue<DateTimeOffset>(7),reader.GetFieldValue<DateTimeOffset>(8)));
        var events=new List<ApplicantRecoveryEvent>();
        await using(var command=await Command("SELECT e.actor_id,e.action,e.detail,e.occurred_utc FROM requestportal_identity_recovery_events e JOIN requestportal_identity_recoveries r ON r.id=e.recovery_id WHERE r.applicant_id=@id ORDER BY e.occurred_utc DESC LIMIT 100",("id",id)))
        await using(var reader=await command.ExecuteReaderAsync(ct)) while(await reader.ReadAsync(ct)) events.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetFieldValue<DateTimeOffset>(3)));
        return new(account,requestCount,requests,memberships,recoveries,events);
    }
    private async Task<ApplicationUser> Applicant(Guid id, CancellationToken ct)
    {
        var user=await db.Users.AsNoTracking().SingleOrDefaultAsync(u=>u.Id==id && !u.IsInternalStaff,ct) ?? throw new InvalidOperationException("Applicant account not found.");
        var account=accounts.Public.Find(id.ToString());
        if(account is not null && (account.Population!=AccountPopulation.PublicApplicant || account.Status!=SecurityAccountStatus.Active))
            throw new InvalidOperationException("Recovery cannot activate an inactive account. Resolve its lifecycle status through the authorized account process first.");
        return user;
    }
    private static void Text(string value, string field) { if(string.IsNullOrWhiteSpace(value)||value.Trim().Length<10||value.Length>2000) throw new InvalidOperationException(field+" must contain 10–2000 characters."); }
    public async Task<Guid> InitiateAsync(ClaimsPrincipal actor, Guid applicantId, string provider, string reason, CancellationToken ct=default)
    {
        string staff=await RequireAsync(actor,RequestPortalPermissions.IdentityRecoveryInitiate,true); Text(reason,"Recovery reason");
        if(!(await ProvidersAsync()).Contains(provider)) throw new InvalidOperationException("The intended provider is not configured.");
        var user=await Applicant(applicantId,ct); if(user.Id.ToString()==staff) throw new UnauthorizedAccessException();
        var id=Guid.NewGuid();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await using(var expired = await Command("""
            WITH expired AS (UPDATE requestportal_identity_recoveries SET status='Cancelled',token_hash=NULL
                WHERE applicant_id=@applicant AND status IN ('Pending','Approved') AND expires_utc<=@now RETURNING id)
            INSERT INTO requestportal_identity_recovery_events SELECT @event,id,@actor,'RecoveryExpired','Unredeemed recovery expired.',@now FROM expired
            """,("applicant",applicantId),("now",Now),("actor",staff),("event",Guid.NewGuid())))
            await expired.ExecuteNonQueryAsync(ct);
        await using(var command=await Command("""
            WITH created AS (INSERT INTO requestportal_identity_recoveries(id,applicant_id,provider,reason,initiated_by,created_utc,expires_utc,status)
            VALUES(@id,@applicant,@provider,@reason,@actor,@now,@expires,'Pending') ON CONFLICT DO NOTHING RETURNING id)
            INSERT INTO requestportal_identity_recovery_events SELECT @event,id,@actor::text,'RecoveryInitiated',@reason,@now FROM created RETURNING recovery_id
            """,("applicant",applicantId),("id",id),("provider",provider),("reason",reason.Trim()),("actor",Guid.Parse(staff)),("now",Now),("expires",Now.AddHours(24)),("event",Guid.NewGuid())))
            if(await command.ExecuteScalarAsync(ct) is not Guid) throw new InvalidOperationException("An open recovery already exists. Cancel it or resolve its completion before starting another.");
        await transaction.CommitAsync(ct);
        return id;
    }
    public async Task<string> ApproveAsync(ClaimsPrincipal actor, Guid applicantId, Guid recoveryId, string verification, bool independentlyVerified, CancellationToken ct=default)
    {
        string staff=await RequireAsync(actor,RequestPortalPermissions.IdentityRecoveryApprove,true); Text(verification,"Independent verification case reference and method");
        if(!independentlyVerified) throw new InvalidOperationException("Independent applicant ownership verification is required. Matching email addresses are insufficient.");
        await Applicant(applicantId,ct);
        string token=recoveryId.ToString("N")+"."+Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        await using var command=await Command("""
            WITH approved AS (UPDATE requestportal_identity_recoveries SET status='Approved',approved_by=@actor,verification_reference=@verification,
                approved_utc=@now,expires_utc=@expires,token_hash=@hash WHERE id=@id AND applicant_id=@applicant AND status='Pending' AND expires_utc>@now AND initiated_by<>@actor RETURNING id)
            INSERT INTO requestportal_identity_recovery_events SELECT @event,id,@actor::text,'OwnershipVerifiedAndRecoveryApproved',@verification,@now FROM approved RETURNING recovery_id
            """,("id",recoveryId),("applicant",applicantId),("actor",Guid.Parse(staff)),("verification",verification.Trim()),("now",Now),("expires",Now.AddMinutes(30)),("hash",Hash(token)),("event",Guid.NewGuid()));
        if(await command.ExecuteScalarAsync(ct) is not Guid) throw new InvalidOperationException("Approval requires a different administrator and an unexpired pending recovery.");
        return token;
    }
    public async Task CancelAsync(ClaimsPrincipal actor, Guid applicantId, Guid recoveryId, string reason, CancellationToken ct=default)
    {
        string staff=await RequireAsync(actor,RequestPortalPermissions.IdentityRecoveryInitiate,true); Text(reason,"Cancellation reason");
        await using var command=await Command("""
            WITH cancelled AS (UPDATE requestportal_identity_recoveries SET status='Cancelled',token_hash=NULL WHERE id=@id AND applicant_id=@applicant AND status IN ('Pending','Approved') RETURNING id)
            INSERT INTO requestportal_identity_recovery_events SELECT @event,id,@actor,'RecoveryCancelled',@reason,@now FROM cancelled RETURNING recovery_id
            """,("id",recoveryId),("applicant",applicantId),("actor",staff),("reason",reason.Trim()),("now",Now),("event",Guid.NewGuid()));
        if(await command.ExecuteScalarAsync(ct) is not Guid) throw new InvalidOperationException("This recovery can no longer be cancelled.");
    }
    private static string Hash(string token)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public async Task<ApplicantRecoveryInvitation?> InvitationAsync(string? token, CancellationToken ct=default)
    {
        if(string.IsNullOrWhiteSpace(token) || token.Length>100 || token.Split('.') is not [var key,var secret] || !Guid.TryParseExact(key,"N",out var id) || secret.Length!=43) return null;
        ApplicantRecoveryInvitation invitation;
        Guid initiator, approver;
        await using(var command=await Command("SELECT applicant_id,provider,initiated_by,approved_by FROM requestportal_identity_recoveries WHERE id=@id AND token_hash=@hash AND status='Approved' AND expires_utc>@now",("id",id),("hash",Hash(token)),("now",Now)))
        await using(var reader=await command.ExecuteReaderAsync(ct))
        {
            if(!await reader.ReadAsync(ct) || reader.IsDBNull(3)) return null;
            invitation=new(id,reader.GetGuid(0),reader.GetString(1)); initiator=reader.GetGuid(2); approver=reader.GetGuid(3);
        }
        if(!await DecisionStillAuthorized(initiator,RequestPortalPermissions.IdentityRecoveryInitiate,ct) ||
            !await DecisionStillAuthorized(approver,RequestPortalPermissions.IdentityRecoveryApprove,ct)) return null;
        return invitation;
    }
    private async Task<bool> DecisionStillAuthorized(Guid id,string permission,CancellationToken ct)
    {
        if(accounts.Staff.Find(id.ToString()) is not { Status:SecurityAccountStatus.Active,Population:AccountPopulation.Staff }) return false;
        await using var groups=await Command("SELECT groups FROM requestportal_staff_signins WHERE account_id=@id",("id",id.ToString()));
        var observed=(await groups.ExecuteScalarAsync(ct)) as string[] ?? [];
        var subject=AuthorizationSubject.Create(id.ToString(),observed);
        return (await authorizer.AuthorizeAsync(subject,RequestPortalPermissions.ManageSecurity,ct)).Allowed &&
            (await authorizer.AuthorizeAsync(subject,permission,ct)).Allowed;
    }

    public async Task<ApplicationUser> CompleteAsync(string token, VerifiedIdentity proof, CancellationToken ct=default)
    {
        var invitation=await InvitationAsync(token,ct) ?? throw new InvalidOperationException("Recovery invitation is invalid, expired, cancelled, or already used.");
        if(proof.Key.ProviderId!=invitation.Provider || proof.Key.ScopeId!=PublicExternalIdentity.Scope(invitation.Provider) || proof.ProviderKind!=AuthenticationProviderKind.External || proof.AuthenticatedAt<Now.AddMinutes(-5) || proof.AuthenticatedAt>Now.AddMinutes(2)) throw new InvalidOperationException("The verified identity does not match the approved provider.");
        var user=await Applicant(invitation.ApplicantId,ct);
        // Burn the invitation before crossing Common.Security's independent atomic binding boundary.
        // A crash after this point fails closed: it cannot link a second identity or replay the token.
        await using(var command=await Command("""
            WITH consumed AS (UPDATE requestportal_identity_recoveries SET status='Redeeming',consumed_utc=@now,token_hash=NULL WHERE id=@id AND status='Approved' AND expires_utc>@now AND token_hash=@hash RETURNING id)
            INSERT INTO requestportal_identity_recovery_events SELECT @event,id,@actor,'RecoveryInvitationConsumed','Correlated OAuth provider proof verified; invitation invalidated.',@now FROM consumed RETURNING recovery_id
            """,("id",invitation.RecoveryId),("hash",Hash(token)),("now",Now),("event",Guid.NewGuid()),("actor",user.Id.ToString())))
            if(await command.ExecuteScalarAsync(ct) is not Guid) throw new InvalidOperationException("Recovery invitation has already been used.");
        try
        {
            string accountId=user.Id.ToString();
            if(accounts.Public.Find(accountId) is null)
                accounts.Public.Create(new(accountId,user.UserName ?? accountId,user.DisplayName,AuthenticationProviderKind.External,AccountPopulation.PublicApplicant,SecurityAccountStatus.Active,proof.Key),accountId);
            else accounts.Public.Link(accountId,proof,accountId);
        }
        catch(Exception error) when(error is InvalidOperationException or System.Data.Common.DbException)
        {
            await FinishAsync(invitation,"Failed",error is IdentityAlreadyLinkedException ? "ProviderAlreadyLinkedElsewhere" : "RecoveryLinkRejected",
                error is IdentityAlreadyLinkedException ? "The verified provider identity is already linked to another account. Its binding was not moved; a new independently reviewed decision is required."
                    : "The verified provider identity could not be linked. Check for an enrollment conflict or inactive account; a new approved invitation is required.",ct);
            throw new InvalidOperationException("This identity could not be linked. Contact support with your recovery reference; no account was replaced.");
        }
        await FinishAsync(invitation,"Completed","ApplicantIdentityRecovered","Verified provider linked to the original applicant account. Existing account relationships preserved.",ct);
        return user;
    }
    private async Task FinishAsync(ApplicantRecoveryInvitation invitation,string status,string action,string detail,CancellationToken ct)
    {
        await using var transaction=await db.Database.BeginTransactionAsync(ct);
        await using(var command=await Command("""
            UPDATE requestportal_identity_recoveries SET status=@status,completed_utc=@now WHERE id=@id AND status='Redeeming';
            INSERT INTO requestportal_identity_recovery_events VALUES(@event,@id,@actor,@action,@detail,@now)
            """,("id",invitation.RecoveryId),("status",status),("now",Now),("event",Guid.NewGuid()),("actor",invitation.ApplicantId.ToString()),("action",action),("detail",detail)))
        { command.Transaction=(NpgsqlTransaction)transaction.GetDbTransaction(); await command.ExecuteNonQueryAsync(ct); }
        if(status=="Completed")
        {
            var notification=new ApplicantIdentityRecoveredMessage(Guid.NewGuid(),Now,invitation.ApplicantId,invitation.Provider);
            db.OutboxMessages.Add(new() { Id=notification.MessageId,MessageType=typeof(ApplicantIdentityRecoveredMessage).FullName!,Payload=JsonSerializer.Serialize(notification),OccurredUtc=Now });
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }
}
