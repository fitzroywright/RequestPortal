using System;
using System.Collections.Generic;
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
    private static readonly HashSet<string> AllowedGenders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Male", "Female", "Non-Binary"
    };

    private static readonly HashSet<string> AllowedMaritalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Single", "Married", "Divorced", "Widowed"
    };

    private static readonly HashSet<string> AllowedEmploymentStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Employed", "Self Employed", "Unemployed", "Student"
    };

    private static readonly HashSet<string> AllowedPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "None", "PATH", "Poor Relief Dole", "Rehabilitation Programme", "Solidarity Program", "CARE"
    };

    private static readonly HashSet<string> AllowedHelpCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Food", "Housing", "Earn an Income", "Education", "Health", "Water & Hygiene"
    };

    private readonly RequestPortalDbContext dbContext;

    public QuickIntakeService(RequestPortalDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task<QuickIntakeView?> GetAsync(
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        QuickIntakeRecord? intake = await dbContext.QuickIntakes
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.RequestId == requestId, cancellationToken);

        if (intake is null) return null;

        return new QuickIntakeView(
            intake.RequestId,
            intake.FirstName,
            intake.LastName,
            intake.DateOfBirth,
            intake.Gender,
            intake.MaritalStatus,
            intake.MobileNumber,
            intake.Email,
            intake.StreetAddress,
            intake.CommunityDistrict,
            intake.Parish,
            intake.EmploymentStatus,
            intake.MonthlyIncome,
            intake.HasChildrenUnder18LivingFullTime,
            intake.HasDisabilityOrSeriousMedicalCondition,
            ParsePrograms(intake.AssistancePrograms),
            intake.HelpCategory,
            intake.CreatedUtc,
            intake.ConvertedToUserId,
            intake.ConvertedUtc,
            intake.SecondaryTelephone);
    }

    public async Task<QuickIntakeResult> CreateAsync(
        QuickIntakeSubmission submission,
        CancellationToken cancellationToken = default,
        Guid? requestId = null,
        Guid? applicantUserId = null)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (requestId.HasValue && requestId.Value == Guid.Empty)
            throw new ArgumentException("Invalid submission identifier.");
        if (requestId.HasValue)
        {
            var existing = await dbContext.QuickIntakes.AsNoTracking()
                .Where(item => item.RequestId == requestId.Value)
                .Select(item => new { item.RequestId, item.MobileNumber, item.ConvertedToUserId })
                .SingleOrDefaultAsync(cancellationToken);
            if (existing is not null)
            {
                if (existing.ConvertedToUserId != applicantUserId)
                    throw new ArgumentException("This submission belongs to a different applicant.");
                string reference = await dbContext.AssistanceRequests.AsNoTracking()
                    .Where(item => item.Id == existing.RequestId)
                    .Select(item => item.PublicReference)
                    .SingleAsync(cancellationToken);
                return new QuickIntakeResult(existing.RequestId, reference, existing.MobileNumber);
            }
        }

        string firstName = Require(submission.FirstName, "First name");
        string lastName = Require(submission.LastName, "Last name");
        if (submission.DateOfBirth == default || submission.DateOfBirth > DateOnly.FromDateTime(DateTime.UtcNow))
            throw new ArgumentException("Enter a valid date of birth.");

        string gender = RequireAllowed(submission.Gender, "Gender", AllowedGenders);
        string maritalStatus = RequireAllowed(submission.MaritalStatus, "Marital status", AllowedMaritalStatuses);
        string mobile = NormalizeMobile(submission.Telephone);
        string? secondaryTelephone = Normalize(submission.SecondaryTelephone);
        if (secondaryTelephone is not null)
        {
            if (secondaryTelephone.Length > 40)
                throw new ArgumentException("Secondary phone number cannot exceed 40 characters.");
            try { secondaryTelephone = NormalizeMobile(secondaryTelephone); }
            catch (ArgumentException)
            {
                throw new ArgumentException("Enter a valid secondary phone number including country code.");
            }
        }
        string? email = Normalize(submission.Email);
        string streetAddress = Require(submission.StreetAddress, "Street address");
        string communityDistrict = Require(submission.CommunityDistrict, "Community/District");
        string parish = Require(submission.Parish, "Parish");
        string employmentStatus = RequireAllowed(submission.EmploymentStatus, "Employment status", AllowedEmploymentStatuses);

        if (submission.MonthlyIncome < 0)
            throw new ArgumentException("Monthly income cannot be negative.");

        string? victimCircumstances = submission.VictimCircumstances is null
            ? null
            : QuickIntakeVictimCircumstances.Normalize(submission.VictimCircumstances);
        string programs = NormalizePrograms(submission.AssistancePrograms);
        string helpCategory = RequireAllowed(submission.HelpCategory, "What do you need help with today", AllowedHelpCategories);
        string displayName = $"{firstName} {lastName}";
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string location = BuildLocation(streetAddress, communityDistrict, parish);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        ApplicationUser? signedInApplicant = applicantUserId.HasValue
            ? await dbContext.Users.SingleOrDefaultAsync(item => item.Id == applicantUserId.Value, cancellationToken)
                ?? throw new ArgumentException("The signed-in applicant could not be found.")
            : null;
        Guid anonymousUserId = signedInApplicant?.Id ?? Guid.NewGuid();
        string internalUserName = $"quick-{anonymousUserId:N}@requestportal.local";
        ApplicationUser anonymousUser = new()
        {
            Id = anonymousUserId,
            UserName = internalUserName,
            NormalizedUserName = internalUserName.ToUpperInvariant(),
            DisplayName = displayName,
            PhoneNumber = mobile,
            PhoneNumberConfirmed = false,
            Email = email,
            NormalizedEmail = email?.ToUpperInvariant(),
            EmailConfirmed = false,
            CreatedUtc = now,
            IndividualProfileUpdateRequired = true,
            IndividualProfileUpdateRequiredUtc = now,
            DateOfBirth = submission.DateOfBirth,
            Gender = gender,
            MaritalStatus = maritalStatus,
            HomeAddress = streetAddress,
            DistrictArea = communityDistrict,
            Parish = parish,
            Town = communityDistrict,
            EmploymentStatus = employmentStatus,
            MonthlyHouseholdIncome = submission.MonthlyIncome,
            ChildrenUnder18 = submission.HasChildrenUnder18LivingFullTime ? 1 : 0
        };
        if (signedInApplicant is null) dbContext.Users.Add(anonymousUser);

        Organization? existingContext = signedInApplicant is null ? null
            : await dbContext.Organizations.Include(item => item.Memberships)
                .Where(item => item.OrganizationType == "IndividualContext"
                    && item.RegisteredByUserId == signedInApplicant.Id
                    && item.Status == OrganizationStatus.Approved)
                .OrderBy(item => item.CreatedUtc).FirstOrDefaultAsync(cancellationToken);
        if (existingContext is not null && !existingContext.Memberships.Any(item =>
            item.UserId == anonymousUserId && item.Status == MembershipStatus.Approved))
            throw new InvalidOperationException("Your individual request context does not have an active membership.");

        Organization individualContext = existingContext ?? new Organization(
            Guid.NewGuid(), signedInApplicant is null ? $"Quick Intake - {displayName}" : displayName,
            "JM", anonymousUserId, now);
        if (existingContext is null)
        {
            individualContext.UpdateProfile(null, "IndividualContext", location, mobile, email, displayName, null);
            individualContext.RequestMembership(Guid.NewGuid(), anonymousUserId, now);
            dbContext.Organizations.Add(individualContext);
            individualContext.Approve(anonymousUserId, now);
        }

        long number = await dbContext.Database
            .SqlQueryRaw<long>("SELECT nextval('request_code_sequence') AS \"Value\"")
            .SingleAsync(cancellationToken);
        string publicReference = $"RP-{now:yy}-{number:D5}";

        AssistanceRequest request = new(
            requestId ?? Guid.NewGuid(),
            publicReference,
            individualContext.Id,
            anonymousUserId,
            now);
        request.SetSummary(helpCategory);
        request.SetDetails(
            null,
            helpCategory,
            null,
            null,
            RequestPriority.Normal,
            location,
            $"Submitted through anonymous Quick Intake. Current programmes: {programs}. Victim circumstances: {victimCircumstances ?? "(not captured)"}. Applicant is being tracked by telephone until converted to a normal Individual account.");

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
            helpCategory,
            HelpCategoryDescription(helpCategory),
            1,
            unit.Id,
            "Captured through Quick Intake."));

        request.Submit(anonymousUserId, now);
        dbContext.AssistanceRequests.Add(request);

        NotificationPreference? whatsapp = await dbContext.NotificationPreferences
            .SingleOrDefaultAsync(item => item.UserId == anonymousUserId && item.Channel == NotificationChannel.WhatsApp, cancellationToken);
        if (whatsapp is null)
            dbContext.NotificationPreferences.Add(new NotificationPreference(
                Guid.NewGuid(), anonymousUserId, NotificationChannel.WhatsApp, mobile, enabled: true));

        QuickIntakeRecord intake = new QuickIntakeRecord(
            Guid.NewGuid(),
            request.Id,
            anonymousUserId,
            individualContext.Id,
            firstName,
            lastName,
            submission.DateOfBirth,
            gender,
            maritalStatus,
            mobile,
            email,
            streetAddress,
            communityDistrict,
            parish,
            employmentStatus,
            submission.MonthlyIncome,
            submission.HasChildrenUnder18LivingFullTime,
            submission.HasDisabilityOrSeriousMedicalCondition,
            programs,
            helpCategory,
            now,
            secondaryTelephone);
        if (signedInApplicant is not null)
            intake.MarkConverted(signedInApplicant.Id, signedInApplicant.Id, now);
        dbContext.QuickIntakes.Add(intake);

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

    public async Task<Guid> GetAnonymousUploaderIdAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        return await dbContext.QuickIntakes.AsNoTracking()
            .Where(item => item.RequestId == requestId)
            .Select(item => item.AnonymousUserId)
            .SingleAsync(cancellationToken);
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

        string location = BuildLocation(intake.StreetAddress, intake.CommunityDistrict, intake.Parish);
        context.Rename(displayName);
        context.UpdateProfile(
            null,
            "IndividualContext",
            location,
            string.IsNullOrWhiteSpace(targetUser.PhoneNumber) ? intake.MobileNumber : targetUser.PhoneNumber,
            string.IsNullOrWhiteSpace(targetUser.Email) ? intake.Email : targetUser.Email,
            displayName,
            null);

        if (string.IsNullOrWhiteSpace(targetUser.DisplayName)) targetUser.DisplayName = displayName;
        if (string.IsNullOrWhiteSpace(targetUser.PhoneNumber)) targetUser.PhoneNumber = intake.MobileNumber;
        if (string.IsNullOrWhiteSpace(targetUser.Email) && !string.IsNullOrWhiteSpace(intake.Email))
        {
            targetUser.Email = intake.Email;
            targetUser.NormalizedEmail = intake.Email.ToUpperInvariant();
        }
        targetUser.DateOfBirth ??= intake.DateOfBirth;
        targetUser.Gender ??= intake.Gender;
        targetUser.MaritalStatus ??= intake.MaritalStatus;
        targetUser.HomeAddress ??= intake.StreetAddress;
        targetUser.DistrictArea ??= intake.CommunityDistrict;
        targetUser.Parish ??= intake.Parish;
        targetUser.Town ??= intake.CommunityDistrict;
        targetUser.EmploymentStatus ??= intake.EmploymentStatus;
        targetUser.MonthlyHouseholdIncome ??= intake.MonthlyIncome;
        targetUser.ChildrenUnder18 ??= intake.HasChildrenUnder18LivingFullTime ? 1 : 0;

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

    private static string RequireAllowed(string? value, string label, HashSet<string> allowed)
    {
        string normalized = Require(value, label);
        string? canonical = allowed.FirstOrDefault(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
        return canonical ?? throw new ArgumentException($"{label} contains an invalid selection.");
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string BuildLocation(string streetAddress, string communityDistrict, string parish) =>
        $"{streetAddress}, {communityDistrict}, {parish}";

    private static string NormalizePrograms(IReadOnlyList<string> values)
    {
        if (values is null || values.Count == 0)
            throw new ArgumentException("Select at least one programme or None.");

        List<string> normalized = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => AllowedPrograms.FirstOrDefault(item => string.Equals(item, value.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("Current assistance programmes contain an invalid selection."))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count == 0)
            throw new ArgumentException("Select at least one programme or None.");

        if (normalized.Contains("None", StringComparer.OrdinalIgnoreCase) && normalized.Count > 1)
            throw new ArgumentException("None cannot be selected with another programme.");

        return string.Join("|", normalized);
    }

    private static IReadOnlyList<string> ParsePrograms(string value) =>
        value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string HelpCategoryDescription(string category) => category switch
    {
        "Food" => "Food package, backyard garden, or related food assistance.",
        "Housing" => "Homelessness, home repairs, or related housing assistance.",
        "Earn an Income" => "Income generation, starting a business, reintegration after prison, or related livelihood assistance.",
        "Education" => "Scholarship, band camp, school, or related education assistance.",
        "Health" => "Wheelchair, medical supplies, or related health assistance.",
        "Water & Hygiene" => "Water tank, toiletries, or related water and hygiene assistance.",
        _ => category
    };

    private static string NormalizeMobile(string value)
    {
        string raw = Require(value, "Telephone");
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
            throw new ArgumentException("Enter a valid telephone number including country code.");

        return compact;
    }
}
