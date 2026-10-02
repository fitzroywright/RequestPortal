using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Administration;
using RequestPortal.Application.Auditing;
using RequestPortal.Common.Messaging;
using RequestPortal.Common.Time;
using RequestPortal.Domain.Organizations;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Administration;

public sealed class StaffOrganizationService : IStaffOrganizationService
{
    private readonly RequestPortalDbContext dbContext;
    private readonly IClock clock;
    private readonly IBusinessAuditWriter auditWriter;

    public StaffOrganizationService(
        RequestPortalDbContext dbContext,
        IClock clock,
        IBusinessAuditWriter auditWriter)
    {
        this.dbContext = dbContext;
        this.clock = clock;
        this.auditWriter = auditWriter;
    }

    public async Task<IReadOnlyList<StaffOrganizationSummary>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        List<StaffOrganizationSummary> organizations = await dbContext.Organizations
            .AsNoTracking()
            .Where(item => item.OrganizationType != "IndividualContext")
            .OrderBy(item => item.Name)
            .Select(item => new StaffOrganizationSummary(
                item.Id,
                item.Name,
                item.CountryCode,
                item.Status.ToString(),
                item.RegistrationNumber,
                dbContext.OrganizationMemberships.Count(membership =>
                    membership.OrganizationId == item.Id && membership.Status == MembershipStatus.Approved),
                dbContext.AssistanceRequests.Count(request => request.OrganizationId == item.Id)))
            .ToListAsync(cancellationToken);

        return organizations;
    }

    public async Task<StaffOrganizationDetail?> GetAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        Organization? organization = await dbContext.Organizations
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == organizationId, cancellationToken);

        if (organization is null)
        {
            return null;
        }

        List<StaffOrganizationMember> members = await (
            from membership in dbContext.OrganizationMemberships.AsNoTracking()
            join user in dbContext.Users.AsNoTracking() on membership.UserId equals user.Id
            where membership.OrganizationId == organizationId
            orderby user.DisplayName
            select new StaffOrganizationMember(
                user.Id,
                user.DisplayName,
                user.Email ?? string.Empty,
                membership.Status.ToString(),
                membership.Role.ToString()))
            .ToListAsync(cancellationToken);

        List<StaffOrganizationRequest> requests = await dbContext.AssistanceRequests
            .AsNoTracking()
            .Where(request => request.OrganizationId == organizationId)
            .OrderByDescending(request => request.CreatedUtc)
            .Select(request => new StaffOrganizationRequest(
                request.Id,
                request.PublicReference,
                request.Status.ToString(),
                request.Summary,
                request.CreatedUtc))
            .ToListAsync(cancellationToken);

        List<StaffAuditEntry> audit = await dbContext.AuditEntries
            .AsNoTracking()
            .Where(entry => entry.OrganizationId == organizationId)
            .OrderByDescending(entry => entry.OccurredUtc)
            .Take(100)
            .Select(entry => new StaffAuditEntry(
                entry.Action,
                entry.EntityType,
                entry.EntityId,
                entry.ActorUserId,
                entry.OccurredUtc,
                entry.Detail))
            .ToListAsync(cancellationToken);

        return new StaffOrganizationDetail(
            organization.Id,
            organization.Name,
            organization.CountryCode,
            organization.Status.ToString(),
            organization.RegistrationNumber,
            organization.OrganizationType,
            organization.Address,
            organization.Phone,
            organization.Email,
            organization.PrimaryContactName,
            organization.RegistrationExpiryDate,
            organization.CreatedUtc,
            organization.ApprovedUtc,
            members,
            requests,
            audit);
    }

    public async Task RejectAsync(
        Guid staffUserId,
        Guid organizationId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        Organization organization = await LoadAsync(organizationId, cancellationToken);
        organization.Reject(staffUserId, reason);
        AddOrganizationStatusOutbox(organization, "Rejected", reason);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(
            staffUserId,
            "OrganizationRejected",
            "Organization",
            organizationId,
            organizationId,
            reason,
            cancellationToken);
    }

    public async Task SuspendAsync(
        Guid staffUserId,
        Guid organizationId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        Organization organization = await LoadAsync(organizationId, cancellationToken);
        organization.Suspend(reason);
        AddOrganizationStatusOutbox(organization, "Suspended", reason);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(
            staffUserId,
            "OrganizationSuspended",
            "Organization",
            organizationId,
            organizationId,
            reason,
            cancellationToken);
    }

    public async Task ReactivateAsync(
        Guid staffUserId,
        Guid organizationId,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        Organization organization = await LoadAsync(organizationId, cancellationToken);
        organization.Reactivate(notes);
        AddOrganizationStatusOutbox(organization, "Reactivated", notes);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(
            staffUserId,
            "OrganizationReactivated",
            "Organization",
            organizationId,
            organizationId,
            notes,
            cancellationToken);
    }

    private void AddOrganizationStatusOutbox(
        Organization organization,
        string status,
        string? detail)
    {
        OrganizationStatusChangedMessage message = new(
            Guid.NewGuid(),
            clock.UtcNow,
            organization.Id,
            organization.RegisteredByUserId,
            status,
            detail);

        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Id = message.MessageId,
            MessageType = typeof(OrganizationStatusChangedMessage).FullName ?? nameof(OrganizationStatusChangedMessage),
            Payload = System.Text.Json.JsonSerializer.Serialize(message),
            OccurredUtc = message.OccurredUtc,
            AttemptCount = 0
        });
    }

    private Task<Organization> LoadAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        return dbContext.Organizations.SingleAsync(item => item.Id == organizationId, cancellationToken);
    }
}
