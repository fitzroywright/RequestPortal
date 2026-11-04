using System.Security.Claims;
using Common.Security.Hosting;
using System.Data.Common;
using Common.Security.Abstractions;
using Common.Security.Integration;
using Common.Security.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Web.Security;

/// <summary>Application business-record projection; Common.Security owns identity resolution and transactions.</summary>
public sealed class RequestPortalAccounts
{
    public RelationalAccountStore Staff { get; }
    public RelationalAccountStore Public { get; }
    public RequestPortalAccounts(string connectionString)
    {
        connectionStringForMetadata = connectionString;
        Staff = new(() => new NpgsqlConnection(connectionString), "RequestPortal.Staff", provisionApplicationRecord: Project);
        Public = new(() => new NpgsqlConnection(connectionString), "RequestPortal.Public", provisionApplicationRecord: Project);
    }
    public void EnsureSchema()
    {
        Staff.EnsureSchema(); Public.EnsureSchema();
        using var connection = new NpgsqlConnection(connectionStringForMetadata);
        connection.Open();
        using var command = new NpgsqlCommand("CREATE TABLE IF NOT EXISTS requestportal_staff_signins(account_id text PRIMARY KEY, groups text[] NOT NULL, last_sign_in timestamptz NOT NULL)",connection);
        command.ExecuteNonQuery();
    }
    private readonly string connectionStringForMetadata;
    public string? FindStaffBinding(ExternalIdentityKey key)
    {
        using var connection = new NpgsqlConnection(connectionStringForMetadata);
        connection.Open();
        using var command = new NpgsqlCommand("""
            SELECT account_id FROM common_security_identities
            WHERE application_id='RequestPortal.Staff' AND provider_id=@provider AND scope_id=@scope AND subject_id=@subject
            """, connection);
        command.Parameters.AddWithValue("provider", key.ProviderId);
        command.Parameters.AddWithValue("scope", key.ScopeId);
        command.Parameters.AddWithValue("subject", key.SubjectId);
        return command.ExecuteScalar() as string;
    }
    public void RecordStaffSignIn(SecurityAccount account, VerifiedIdentity proof)
    {
        using var connection = new NpgsqlConnection(connectionStringForMetadata); connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = new NpgsqlCommand("""
            UPDATE "AspNetUsers" SET "LastSignInUtc"=@now, "Email"=COALESCE(@email,"Email") WHERE "Id"=@id AND "IsInternalStaff";
            INSERT INTO requestportal_staff_signins(account_id,groups,last_sign_in) VALUES(@account,@groups,@now)
            ON CONFLICT(account_id) DO UPDATE SET groups=EXCLUDED.groups,last_sign_in=EXCLUDED.last_sign_in;
            INSERT INTO common_security_account_events(application_id,event_id,account_id,actor_id,action,occurred_at)
            VALUES('RequestPortal.Staff',@event,@account,@account,'StaffAuthenticated',@time);
            """,connection,transaction);
        command.Parameters.AddWithValue("now",DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("email",NpgsqlTypes.NpgsqlDbType.Text,(object?)proof.VerifiedEmail ?? DBNull.Value);
        command.Parameters.AddWithValue("id",Guid.Parse(account.Id));
        command.Parameters.AddWithValue("account",account.Id);
        command.Parameters.AddWithValue("groups",proof.GroupNames.ToArray());
        command.Parameters.AddWithValue("event",Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("time",DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery(); transaction.Commit();
    }
    /// <summary>Serialize application unlink decisions so concurrent requests cannot remove the final configured provider.</summary>
    public async Task UnlinkPublicIdentityAsync(ClaimsPrincipal applicant, ExternalIdentityKey identity, IReadOnlyCollection<string> configuredProviders, CancellationToken cancellationToken = default)
    {
        string? accountId = applicant.FindFirstValue(AccountSecurityHosting.AccountIdClaim);
        string? authenticatedBinding = applicant.FindFirstValue(PublicExternalIdentity.BindingClaim);
        if (applicant.Identity?.IsAuthenticated != true || accountId is null || authenticatedBinding is null ||
            applicant.FindFirstValue(ClaimTypes.NameIdentifier) != accountId ||
            applicant.HasClaim(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue) ||
            !long.TryParse(applicant.FindFirstValue(AccountSecurityHosting.AuthenticationTimeClaim), out long seconds) ||
            seconds < DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds() || seconds > DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds())
            throw new UnauthorizedAccessException("Sign in again using the verified provider you will keep before unlinking an identity.");
        using var connection = new NpgsqlConnection(connectionStringForMetadata);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var guard = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key,0))", connection, transaction))
        {
            guard.Parameters.AddWithValue("key", "RequestPortal.Public.Unlink:" + accountId);
            await guard.ExecuteNonQueryAsync(cancellationToken);
        }
        var account = Public.Find(accountId);
        if (account is not { Status: SecurityAccountStatus.Active, Population: AccountPopulation.PublicApplicant } ||
            !long.TryParse(applicant.FindFirstValue(AccountSecurityHosting.SessionVersionClaim), out long version) || version != account.SessionVersion)
            throw new UnauthorizedAccessException("Sign in again before changing linked identities.");
        var identities = Public.GetIdentities(accountId);
        if (!identities.Any(item => item.Key == identity)) throw new InvalidOperationException("The linked identity was not found on this account.");
        if (!identities.Any(item => item.Key != identity && configuredProviders.Contains(item.Key.ProviderId) && PublicExternalIdentity.BindingFingerprint(item.Key) == authenticatedBinding))
            throw new InvalidOperationException("Sign in using the verified provider you will keep before removing this identity.");
        Public.Unlink(accountId, identity, accountId);
        await transaction.CommitAsync(cancellationToken);
    }

    // Common.Security owns identity uniqueness, business projection and the atomic creation transaction.
    public SecurityAccount CreateStaff(AccountCreation creation, string actor,
        Action<DbConnection, DbTransaction, SecurityAccount> finalize)
    {
        if (creation.Population != AccountPopulation.Staff) throw new InvalidOperationException("Staff provisioning only.");
        var store = new RelationalAccountStore(() => new NpgsqlConnection(connectionStringForMetadata), "RequestPortal.Staff",
            provisionApplicationRecord: (connection, transaction, account, proof) => {
                Project(connection, transaction, account, proof);
                finalize(connection, transaction, account);
            });
        return store.Create(creation, actor);
    }

    private static void Project(DbConnection connection, DbTransaction transaction, SecurityAccount account, VerifiedIdentity? proof)
    {
        using var db = new RequestPortalDbContext(new DbContextOptionsBuilder<RequestPortalDbContext>()
            .UseNpgsql((NpgsqlConnection)connection).Options);
        db.Database.UseTransaction(transaction);
        bool staff = account.Population != AccountPopulation.PublicApplicant;
        Guid id = Guid.Parse(account.Id);
        var existing = db.Users.SingleOrDefault(x => x.Id == id);
        if (existing is not null)
        {
            if (existing.IsInternalStaff != staff) throw new InvalidOperationException("Applicant and staff records cannot be converted during identity enrollment.");
            return; // Approved imports preserve all existing identifiers, profiles and associations.
        }
        string? email = proof?.VerifiedEmail;
        // A contact address may be supplied without being identity proof. It never authorizes linking.
        if (!staff && string.IsNullOrWhiteSpace(email))
            email = proof?.Claims.GetValueOrDefault("contact_email");
        if (!staff && (string.IsNullOrWhiteSpace(email) || db.Users.Any(x => x.NormalizedEmail == email.ToUpperInvariant())))
            throw new IdentityEnrollmentRequiredException();
        string login = staff ? "staff:" + id.ToString("D") : email!;
        var now = DateTimeOffset.UtcNow;
        db.Users.Add(new RequestPortal.Infrastructure.Identity.ApplicationUser {
            Id=id, UserName=login, NormalizedUserName=login.ToUpperInvariant(), Email=staff ? null : email,
            NormalizedEmail=staff ? null : email!.ToUpperInvariant(), EmailConfirmed=proof?.VerifiedEmail is not null,
            SecurityStamp=Guid.NewGuid().ToString("N"), ConcurrencyStamp=Guid.NewGuid().ToString("N"),
            DisplayName=account.DisplayName, IsInternalStaff=staff, DirectoryUserName=staff ? account.LoginName : null,
            CreatedUtc=now, LastSignInUtc=proof is null ? null : now, IndividualProfileUpdateRequired=!staff,
            IndividualProfileUpdateRequiredUtc=staff ? null : now
        });
        db.SaveChanges();
    }
}
