using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Requests;
using RequestPortal.Common.Messaging;
using RequestPortal.Domain.Notifications;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Requests;

public sealed class QuickIntakeService : IQuickIntakeService
{
    private readonly RequestPortalDbContext dbContext;

    public QuickIntakeService(RequestPortalDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task<QuickIntakeView?> GetAsync(
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.QuickIntakes
            .AsNoTracking()
            .Where(item => item.RequestId == requestId)
            .Select(item => new QuickIntakeView(
                item.RequestId,
                item.FirstName,
                item.LastName,
                item.MobileNumber,
                item.Parish,
                item.Town,
                item.CreatedUtc,
                item.ConvertedToUserId,
                item.ConvertedUtc))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<QuickIntakeResult> CreateAsync(
        QuickIntakeSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);

        string firstName = Require(submission.FirstName, "First name");
        string lastName = Require(submission.LastName, "Last name");
        string mobile = NormalizeMobile(submission.MobileNumber);
        string parish = Require(submission.Parish, "Parish");
        string assistanceNeeded = Require(submission.AssistanceNeeded, "Assistance needed");
        string displayName = $"{firstName} {lastName}";
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        Guid anonymousUserId = Guid.NewGuid();
        string internalUserName = $"quick-{anonymousUserId:N}@requestportal.local";
        ApplicationUser anonymousUser = new()
        {
            Id = anonymousUserId,
            UserName = internalUserName,
            NormalizedUserName = internalUserName.ToUpperInvariant(),
            DisplayName = displayName,
            PhoneNumber = mobile,
            PhoneNumberConfirmed = false,
            CreatedUtc = now,
            IndividualProfileUpdateRequired = true,
            IndividualProfileUpdateRequiredUtc = now,
            Parish = parish,
            Town = Normalize(submission.Town)
        };
        dbContext.Users.Add(anonymousUser);

        Organization individualContext = new(
            Guid.NewGuid(),
            $"Quick Intake - {displayName}",
            "JM",
            anonymousUserId,
            now);
        individualContext.UpdateProfile(
            null,
            "IndividualContext",
            BuildLocation(submission.Town, parish),
            mobile,
            null,
            displayName,
            null);

        individualContext.RequestMembership(Guid.NewGuid(), anonymousUserId, now);
        dbContext.Organizations.Add(individualContext);
        individualContext.Approve(anonymousUserId, now);

        long number = await dbContext.Database
            .SqlQueryRaw<long>("SELECT nextval('request_code_sequence') AS \"Value\"")
            .SingleAsync(cancellationToken);
        string publicReference = $"RP-{now:yy}-{number:D5}";

        AssistanceRequest request = new(
            Guid.NewGuid(),
            publicReference,
            individualContext.Id,
            anonymousUserId,
            now);
        request.SetSummary(assistanceNeeded);
        request.SetDetails(
            null,
            Normalize(submission.Category),
            null,
            null,
            RequestPriority.Normal,
            BuildLocation(submission.Town, parish),
            "Submitted through anonymous Quick Intake. Applicant is being tracked by outbound WhatsApp until converted to a normal Individual account.");

        var unit = await dbContext.UnitsOfMeasure
            .AsNoTracking()
            .Where(item => item.IsActive)
            .OrderByDescending(item => item.Code == "EA")
            .ThenBy(item => item.Name)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Quick Intake requires at least one active unit of measure.");

        request.AddItem(new AssistanceRequestItem(
            Guid.NewGuid(),
            request.Id,
            "Assistance requested",
            assistanceNeeded,
            1,
            unit.Id,
            "Captured through Quick Intake."));

        request.Submit(anonymousUserId, now);
        dbContext.AssistanceRequests.Add(request);

        dbContext.NotificationPreferences.Add(new NotificationPreference(
            Guid.NewGuid(),
            anonymousUserId,
            NotificationChannel.WhatsApp,
            mobile,
            enabled: true));

        dbContext.QuickIntakes.Add(new QuickIntakeRecord(
            Guid.NewGuid(),
            request.Id,
            anonymousUserId,
            individualContext.Id,
            firstName,
            lastName,
            mobile,
            parish,
            submission.Town,
            now));

        RequestSubmittedMessage submitted = new(
            Guid.NewGuid(),
            now,
            request.Id,
            individualContext.Id);

        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Id = submitted.MessageId,
            MessageType = typeof(RequestSubmittedMessage).FullName ?? nameof(RequestSubmittedMessage),
            Payload = JsonSerializer.Serialize(submitted),
            OccurredUtc = submitted.OccurredUtc,
            AttemptCount = 0
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new QuickIntakeResult(request.Id, publicReference, mobile);
    }

    public async Task ConvertToIndividualAsync(
        Guid requestId,
        Guid userId,
        Guid staffUserId,
        CancellationToken cancellationToken = default)
    {
        QuickIntakeRecord intake = await dbContext.QuickIntakes
            .SingleOrDefaultAsync(item => item.RequestId == requestId, cancellationToken)
            ?? throw new InvalidOperationException("This request is not an anonymous Quick Intake request.");

        if (intake.IsConverted)
        {
            if (intake.ConvertedToUserId == userId) return;
            throw new InvalidOperationException("This Quick Intake request has already been converted to another user.");
        }

        ApplicationUser targetUser = await dbContext.Users
            .SingleAsync(item => item.Id == userId, cancellationToken);

        AssistanceRequest request = await dbContext.AssistanceRequests
            .SingleAsync(item => item.Id == requestId, cancellationToken);

        Organization context = await dbContext.Organizations
            .Include(item => item.Memberships)
            .SingleAsync(item => item.Id == intake.IndividualContextId, cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        OrganizationMembership? targetMembership = context.Memberships
            .SingleOrDefault(item => item.UserId == userId);

        if (targetMembership is null)
        {
            targetMembership = context.RequestMembership(Guid.NewGuid(), userId, now);
            targetMembership.Approve(staffUserId, now);
            targetMembership.PromoteToAdministrator();
        }
        else if (targetMembership.Status != MembershipStatus.Approved)
        {
            throw new InvalidOperationException("The target individual already has a non-active membership in this context.");
        }
        else if (targetMembership.Role != OrganizationRole.Administrator)
        {
            targetMembership.PromoteToAdministrator();
        }

        OrganizationMembership? anonymousMembership = context.Memberships
            .SingleOrDefault(item => item.UserId == intake.AnonymousUserId && item.Status == MembershipStatus.Approved);
        if (anonymousMembership is not null)
        {
            context.RevokeMembership(
                anonymousMembership.Id,
                staffUserId,
                now,
                "Anonymous Quick Intake converted to an Individual client.");
        }

        string displayName = string.IsNullOrWhiteSpace(targetUser.DisplayName)
            ? $"{intake.FirstName} {intake.LastName}"
            : targetUser.DisplayName.Trim();

        context.Rename(displayName);
        context.UpdateProfile(
            null,
            "IndividualContext",
            BuildLocation(intake.Town, intake.Parish),
            string.IsNullOrWhiteSpace(targetUser.PhoneNumber) ? intake.MobileNumber : targetUser.PhoneNumber,
            targetUser.Email,
            displayName,
            null);

        if (string.IsNullOrWhiteSpace(targetUser.DisplayName))
            targetUser.DisplayName = displayName;
        if (string.IsNullOrWhiteSpace(targetUser.PhoneNumber))
            targetUser.PhoneNumber = intake.MobileNumber;
        if (string.IsNullOrWhiteSpace(targetUser.Parish))
            targetUser.Parish = intake.Parish;
        if (string.IsNullOrWhiteSpace(targetUser.Town))
            targetUser.Town = intake.Town;

        targetUser.IndividualProfileUpdateRequired = true;
        targetUser.IndividualProfileUpdateRequiredUtc ??= now;

        NotificationPreference? targetWhatsApp = await dbContext.NotificationPreferences
            .SingleOrDefaultAsync(
                item => item.UserId == userId && item.Channel == NotificationChannel.WhatsApp,
                cancellationToken);

        if (targetWhatsApp is null)
        {
            dbContext.NotificationPreferences.Add(new NotificationPreference(
                Guid.NewGuid(),
                userId,
                NotificationChannel.WhatsApp,
                intake.MobileNumber,
                enabled: true));
        }
        else
        {
            targetWhatsApp.Update(intake.MobileNumber, enabled: true);
        }

        request.ConvertApplicant(userId);
        intake.MarkConverted(userId, staffUserId, now);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string Require(string? value, string label) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{label} is required.")
            : value.Trim();

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string BuildLocation(string? town, string parish)
    {
        string? normalizedTown = Normalize(town);
        return normalizedTown is null ? parish : $"{normalizedTown}, {parish}";
    }

    private static string NormalizeMobile(string value)
    {
        string raw = Require(value, "WhatsApp mobile number");
        string compact = new(raw.Where(character => char.IsDigit(character) || character == '+').ToArray());

        if (compact.StartsWith("00", StringComparison.Ordinal))
            compact = "+" + compact[2..];

        if (!compact.StartsWith('+'))
        {
            string digits = new(compact.Where(char.IsDigit).ToArray());
            compact = digits.Length == 10 ? $"+1{digits}" : $"+{digits}";
        }

        int digitCount = compact.Count(char.IsDigit);
        if (digitCount is < 10 or > 15)
            throw new ArgumentException("Enter a valid WhatsApp mobile number including country code.");

        return compact;
    }
}
