using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Requests;
using RequestPortal.Common.Time;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Requests;

public sealed class RequestSubmissionPolicy(
    RequestPortalDbContext dbContext,
    IClock clock,
    IOptions<RequestSubmissionPolicyOptions> options)
    : IRequestSubmissionPolicy
{
    private readonly RequestSubmissionPolicyOptions policy = options.Value;

    public async Task ValidateAsync(
        Guid userId,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        if (!policy.Enabled)
        {
            return;
        }

        var request = await (
            from item in dbContext.AssistanceRequests.AsNoTracking()
            join organization in dbContext.Organizations.AsNoTracking()
                on item.OrganizationId equals organization.Id
            where item.Id == requestId
            select new
            {
                item.Id,
                item.OrganizationId,
                item.CreatedByUserId,
                item.Status,
                item.Kind,
                item.Summary,
                OrganizationType = organization.OrganizationType
            })
            .SingleAsync(cancellationToken);

        if (request.CreatedByUserId != userId)
        {
            throw Block(
                "Only the person who created this request may submit it.",
                "RP-SUBMISSION-OWNER");
        }

        var user = await dbContext.Users
            .AsNoTracking()
            .Where(item => item.Id == userId)
            .Select(item => new
            {
                item.Email,
                item.EmailConfirmed,
                item.DisplayName,
                item.IndividualProfileUpdateRequired,
                item.LockoutEnabled,
                item.LockoutEnd
            })
            .SingleAsync(cancellationToken);

        DateTimeOffset now = clock.UtcNow;

        if (user.LockoutEnabled && user.LockoutEnd.HasValue && user.LockoutEnd.Value > now)
        {
            throw Block(
                "This account is temporarily restricted from submitting requests. Please contact Food For The Poor if you need assistance.",
                "RP-SUBMISSION-ACCOUNT-RESTRICTED");
        }

        if (string.IsNullOrWhiteSpace(user.Email) || !user.EmailConfirmed)
        {
            throw Block(
                "A verified sign-in account is required before a request can be submitted.",
                "RP-SUBMISSION-UNVERIFIED");
        }

        if (string.Equals(request.OrganizationType, "IndividualContext", StringComparison.Ordinal) &&
            (string.IsNullOrWhiteSpace(user.DisplayName) || user.IndividualProfileUpdateRequired))
        {
            throw Block(
                "Complete your individual profile before submitting a request.",
                "RP-SUBMISSION-PROFILE-INCOMPLETE");
        }

        RequestStatus[] activeStatuses =
        [
            RequestStatus.Draft,
            RequestStatus.Submitted,
            RequestStatus.MoreInformationRequired,
            RequestStatus.Approved,
            RequestStatus.PendingFulfilment,
            RequestStatus.RepresentativeReviewRequired
        ];

        int otherActiveRequests = await dbContext.AssistanceRequests
            .AsNoTracking()
            .CountAsync(
                item =>
                    item.Id != requestId &&
                    (item.SubmittedByUserId == userId || item.CreatedByUserId == userId) &&
                    activeStatuses.Contains(item.Status),
                cancellationToken);

        if (otherActiveRequests >= policy.MaxActiveRequestsPerUser)
        {
            throw Block(
                $"You already have {otherActiveRequests} active requests. Complete or close an existing request before submitting another.",
                "RP-SUBMISSION-ACTIVE-LIMIT");
        }

        DateTimeOffset dayCutoff = now.AddHours(-24);
        int submissionsLast24Hours = await dbContext.AssistanceRequests
            .AsNoTracking()
            .CountAsync(
                item =>
                    item.Id != requestId &&
                    item.SubmittedByUserId == userId &&
                    item.SubmittedUtc >= dayCutoff,
                cancellationToken);

        if (submissionsLast24Hours >= policy.MaxSubmissionsPer24Hours)
        {
            throw Block(
                $"You have reached the submission limit of {policy.MaxSubmissionsPer24Hours} requests in 24 hours. Please try again later.",
                "RP-SUBMISSION-24H-LIMIT");
        }

        DateTimeOffset monthCutoff = now.AddDays(-30);
        int submissionsLast30Days = await dbContext.AssistanceRequests
            .AsNoTracking()
            .CountAsync(
                item =>
                    item.Id != requestId &&
                    item.SubmittedByUserId == userId &&
                    item.SubmittedUtc >= monthCutoff,
                cancellationToken);

        if (submissionsLast30Days >= policy.MaxSubmissionsPer30Days)
        {
            throw Block(
                $"You have reached the submission limit of {policy.MaxSubmissionsPer30Days} requests in 30 days. Please contact Food For The Poor if you have another urgent need.",
                "RP-SUBMISSION-30D-LIMIT");
        }

        if (policy.DuplicateLookbackDays > 0 && !string.IsNullOrWhiteSpace(request.Summary))
        {
            DateTimeOffset duplicateCutoff = now.AddDays(-policy.DuplicateLookbackDays);
            string normalizedSummary = Normalize(request.Summary);

            var recentCandidates = await dbContext.AssistanceRequests
                .AsNoTracking()
                .Where(item =>
                    item.Id != requestId &&
                    item.OrganizationId == request.OrganizationId &&
                    item.Kind == request.Kind &&
                    item.SubmittedUtc >= duplicateCutoff &&
                    item.Status != RequestStatus.Cancelled &&
                    item.Status != RequestStatus.Duplicate)
                .Select(item => new
                {
                    item.PublicReference,
                    item.Summary
                })
                .ToListAsync(cancellationToken);

            string? duplicateReference = recentCandidates
                .Where(item => Normalize(item.Summary) == normalizedSummary)
                .Select(item => item.PublicReference)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(duplicateReference))
            {
                throw Block(
                    $"A similar request was already submitted recently ({duplicateReference}). Open that request instead, or change the summary if this is a different need.",
                    "RP-SUBMISSION-DUPLICATE");
            }
        }
    }

    private static RequestSubmissionBlockedException Block(
        string message,
        string code) =>
        new(message, code);

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(
            ' ',
            value.Trim()
                .ToLowerInvariant()
                .Split(
                    [' ', '\t', '\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries));
    }
}
