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
        Staff = new(() => new NpgsqlConnection(connectionString), "RequestPortal.Staff", provisionApplicationRecord: Project);
        Public = new(() => new NpgsqlConnection(connectionString), "RequestPortal.Public", provisionApplicationRecord: Project);
    }
    public void EnsureSchema() { Staff.EnsureSchema(); Public.EnsureSchema(); }
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
            CreatedUtc=now, LastSignInUtc=now, IndividualProfileUpdateRequired=!staff,
            IndividualProfileUpdateRequiredUtc=staff ? null : now
        });
        db.SaveChanges();
    }
}
