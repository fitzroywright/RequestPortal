using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Auditing;
using RequestPortal.Application.Organizations;
using RequestPortal.Common.Messaging;
using RequestPortal.Common.Time;
using RequestPortal.Domain.Organizations;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Organizations;

public sealed class OrganizationInvitationService : IOrganizationInvitationService
{
    private readonly RequestPortalDbContext dbContext;
    private readonly INotificationSender notificationSender;
    private readonly IBusinessAuditWriter auditWriter;
    private readonly IClock clock;

    public OrganizationInvitationService(
        RequestPortalDbContext dbContext,
        INotificationSender notificationSender,
        IBusinessAuditWriter auditWriter,
        IClock clock)
    {
        this.dbContext = dbContext;
        this.notificationSender = notificationSender;
        this.auditWriter = auditWriter;
        this.clock = clock;
    }

    public async Task<OrganizationInvitationView> InviteAsync(
        Guid administratorUserId,
        Guid organizationId,
        string email,
        string publicBaseUrl,
        CancellationToken cancellationToken = default)
    {
        await EnsureAdministratorAsync(administratorUserId, organizationId, cancellationToken);

        string normalizedEmail = NormalizeEmail(email);
        bool alreadyMember = await (
            from membership in dbContext.OrganizationMemberships.AsNoTracking()
            join user in dbContext.Users.AsNoTracking()
                on membership.UserId equals user.Id
            where membership.OrganizationId == organizationId
                && membership.Status == MembershipStatus.Approved
                && user.NormalizedEmail == normalizedEmail.ToUpperInvariant()
            select membership.Id)
            .AnyAsync(cancellationToken);

        if (alreadyMember)
        {
            throw new InvalidOperationException("That person is already an approved member of this organization.");
        }

        DateTimeOffset now = clock.UtcNow;

        OrganizationInvitation? existing = await dbContext.Set<OrganizationInvitation>()
            .Where(item =>
                item.OrganizationId == organizationId &&
                item.Email == normalizedEmail &&
                item.Status == InvitationStatus.Pending)
            .OrderByDescending(item => item.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null && existing.ExpiresUtc > now)
        {
            throw new InvalidOperationException("An active invitation has already been sent to this email address.");
        }

        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        OrganizationInvitation invitation = new(
            Guid.NewGuid(),
            organizationId,
            normalizedEmail,
            HashToken(token),
            administratorUserId,
            now,
            now.AddDays(7));

        dbContext.Set<OrganizationInvitation>().Add(invitation);
        await dbContext.SaveChangesAsync(cancellationToken);

        string organizationName = await dbContext.Organizations.AsNoTracking()
            .Where(item => item.Id == organizationId)
            .Select(item => item.Name)
            .SingleAsync(cancellationToken);

        string baseUrl = publicBaseUrl.TrimEnd('/');
        string link = $"{baseUrl}/Organizations/Invitation?id={invitation.Id:D}&token={Uri.EscapeDataString(token)}";

        await notificationSender.SendAsync(
            new NotificationEnvelope(
                normalizedEmail,
                $"{organizationName} invited you to Request Portal",
                $"{organizationName} has invited you to become a representative in Food For The Poor Jamaica Request Portal.\n\nSign in using this email address and accept the invitation here:\n{link}\n\nThis invitation expires {invitation.ExpiresUtc:u}. If you were not expecting this invitation, you can ignore this message.",
                CorrelationId: invitation.Id.ToString("D")),
            cancellationToken);

        await auditWriter.WriteAsync(
            administratorUserId,
            "OrganizationRepresentativeInvited",
            "OrganizationInvitation",
            invitation.Id,
            organizationId,
            $"email={normalizedEmail}; expires={invitation.ExpiresUtc:O}",
            cancellationToken);

        return ToView(invitation, organizationName, now);
    }

    public async Task<IReadOnlyList<OrganizationInvitationView>> GetForAdministratorAsync(
        Guid administratorUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAdministratorAsync(administratorUserId, organizationId, cancellationToken);
        DateTimeOffset now = clock.UtcNow;

        var rows = await (
            from invitation in dbContext.Set<OrganizationInvitation>().AsNoTracking()
            join organization in dbContext.Organizations.AsNoTracking()
                on invitation.OrganizationId equals organization.Id
            where invitation.OrganizationId == organizationId
            orderby invitation.CreatedUtc descending
            select new { Invitation = invitation, OrganizationName = organization.Name })
            .ToListAsync(cancellationToken);

        return rows.Select(row => ToView(row.Invitation, row.OrganizationName, now)).ToArray();
    }

    public async Task<IReadOnlyList<OrganizationInvitationView>> GetPendingForUserAsync(
        Guid userId,
        string? email,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return Array.Empty<OrganizationInvitationView>();
        }

        string normalizedEmail = NormalizeEmail(email);
        DateTimeOffset now = clock.UtcNow;

        var rows = await (
            from invitation in dbContext.Set<OrganizationInvitation>().AsNoTracking()
            join organization in dbContext.Organizations.AsNoTracking()
                on invitation.OrganizationId equals organization.Id
            where invitation.Email == normalizedEmail &&
                  invitation.Status == InvitationStatus.Pending &&
                  invitation.ExpiresUtc > now
            orderby invitation.CreatedUtc descending
            select new { Invitation = invitation, OrganizationName = organization.Name })
            .ToListAsync(cancellationToken);

        return rows.Select(row => ToView(row.Invitation, row.OrganizationName, now)).ToArray();
    }

    public async Task AcceptAsync(
        Guid userId,
        Guid invitationId,
        string token,
        CancellationToken cancellationToken = default)
    {
        var account = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new { user.Email, user.EmailConfirmed })
            .SingleAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(account.Email) || !account.EmailConfirmed)
        {
            throw new InvalidOperationException("Your signed-in account does not have a verified email address.");
        }

        OrganizationInvitation invitation = await dbContext.Set<OrganizationInvitation>()
            .SingleOrDefaultAsync(item => item.Id == invitationId, cancellationToken)
            ?? throw new InvalidOperationException("Invitation was not found.");

        DateTimeOffset now = clock.UtcNow;
        if (invitation.Status != InvitationStatus.Pending || invitation.ExpiresUtc <= now)
        {
            throw new InvalidOperationException("This invitation is no longer active.");
        }

        if (!string.Equals(invitation.Email, NormalizeEmail(account.Email), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This invitation was sent to a different email address. Sign in with the invited account.");
        }

        if (!TokenMatches(invitation.TokenHash, token))
        {
            throw new InvalidOperationException("The invitation link is invalid.");
        }

        Organization organization = await dbContext.Organizations
            .Include(item => item.Memberships)
            .SingleAsync(item => item.Id == invitation.OrganizationId, cancellationToken);

        if (organization.Status != OrganizationStatus.Approved)
        {
            throw new InvalidOperationException("This organization is not currently approved for representative access.");
        }

        OrganizationMembership? existing = organization.Memberships
            .FirstOrDefault(item =>
                item.UserId == userId &&
                item.Status != MembershipStatus.Revoked &&
                item.Status != MembershipStatus.Rejected);

        OrganizationMembership approvedMembership;
        if (existing is null)
        {
            approvedMembership = organization.RequestMembership(
                Guid.NewGuid(),
                userId,
                now);
            approvedMembership.Approve(invitation.InvitedByUserId, now);
            dbContext.OrganizationMemberships.Add(approvedMembership);
        }
        else
        {
            approvedMembership = existing;
            if (approvedMembership.Status != MembershipStatus.Approved)
            {
                approvedMembership.Approve(invitation.InvitedByUserId, now);
            }
        }

        var acceptedUser = await dbContext.Users
            .SingleAsync(user => user.Id == userId, cancellationToken);
        acceptedUser.IndividualProfileUpdateRequired = false;
        acceptedUser.IndividualProfileUpdateRequiredUtc = null;

        MembershipChangedMessage changed = new(
            Guid.NewGuid(),
            now,
            invitation.OrganizationId,
            approvedMembership.Id,
            userId,
            "ApprovedByInvitation",
            invitation.Id.ToString("D"));

        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Id = changed.MessageId,
            MessageType = typeof(MembershipChangedMessage).FullName ?? nameof(MembershipChangedMessage),
            Payload = System.Text.Json.JsonSerializer.Serialize(changed),
            OccurredUtc = changed.OccurredUtc,
            AttemptCount = 0
        });

        invitation.Accept(userId, now);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            userId,
            "OrganizationInvitationAccepted",
            "OrganizationInvitation",
            invitation.Id,
            invitation.OrganizationId,
            $"email={invitation.Email}",
            cancellationToken);
    }

    public async Task RevokeAsync(
        Guid administratorUserId,
        Guid organizationId,
        Guid invitationId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAdministratorAsync(administratorUserId, organizationId, cancellationToken);

        OrganizationInvitation invitation = await dbContext.Set<OrganizationInvitation>()
            .SingleAsync(item =>
                item.Id == invitationId &&
                item.OrganizationId == organizationId,
                cancellationToken);

        invitation.Revoke();
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            administratorUserId,
            "OrganizationInvitationRevoked",
            "OrganizationInvitation",
            invitation.Id,
            organizationId,
            $"email={invitation.Email}",
            cancellationToken);
    }

    private async Task EnsureAdministratorAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        bool administrator = await dbContext.OrganizationMemberships.AsNoTracking()
            .AnyAsync(item =>
                item.OrganizationId == organizationId &&
                item.UserId == userId &&
                item.Status == MembershipStatus.Approved &&
                item.Role == OrganizationRole.Administrator,
                cancellationToken);

        if (!administrator)
        {
            throw new UnauthorizedAccessException("Organization administrator access is required.");
        }
    }

    private static OrganizationInvitationView ToView(
        OrganizationInvitation invitation,
        string organizationName,
        DateTimeOffset now)
    {
        string status = invitation.Status == InvitationStatus.Pending && invitation.ExpiresUtc <= now
            ? InvitationStatus.Expired.ToString()
            : invitation.Status.ToString();

        return new OrganizationInvitationView(
            invitation.Id,
            invitation.OrganizationId,
            organizationName,
            invitation.Email,
            status,
            invitation.CreatedUtc,
            invitation.ExpiresUtc,
            invitation.InvitedByUserId,
            invitation.AcceptedByUserId,
            invitation.AcceptedUtc);
    }

    private static string NormalizeEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            throw new ArgumentException("A valid email address is required.", nameof(email));
        }

        return email.Trim().ToLowerInvariant();
    }

    private static bool TokenMatches(string expectedHash, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        byte[] expected = Convert.FromHexString(expectedHash);
        byte[] actual = Convert.FromHexString(HashToken(token));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
