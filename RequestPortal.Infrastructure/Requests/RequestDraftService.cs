using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Requests;
using RequestPortal.Application.Security;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Requests;

public sealed class RequestDraftService : IRequestDraftService
{
    private readonly RequestPortalDbContext dbContext;
    private readonly IOrganizationAccessService organizationAccessService;

    public RequestDraftService(
        RequestPortalDbContext dbContext,
        IOrganizationAccessService organizationAccessService)
    {
        this.dbContext = dbContext;
        this.organizationAccessService = organizationAccessService;
    }

    public async Task SetDetailsAsync(
        Guid userId,
        Guid requestId,
        Guid? beneficiaryId,
        RequestKind kind,
        string? category,
        string? subcategory,
        string? programCategory,
        RequestPriority priority,
        string? location,
        string? circumstances,
        CancellationToken cancellationToken = default)
    {
        AssistanceRequest request = await dbContext.AssistanceRequests
            .SingleAsync(item => item.Id == requestId, cancellationToken);

        bool allowed = await organizationAccessService.IsApprovedMemberAsync(userId, request.OrganizationId, cancellationToken);
        if (!allowed)
        {
            throw new UnauthorizedAccessException("The current user cannot edit this request.");
        }

        if (beneficiaryId.HasValue)
        {
            bool beneficiaryBelongsToOrganization = await dbContext.Beneficiaries
                .AsNoTracking()
                .AnyAsync(
                    item => item.Id == beneficiaryId.Value && item.OrganizationId == request.OrganizationId,
                    cancellationToken);

            if (!beneficiaryBelongsToOrganization)
            {
                throw new InvalidOperationException("The selected beneficiary does not belong to this organization.");
            }
        }

        request.SetKind(kind);
        request.SetDetails(
            beneficiaryId,
            category,
            subcategory,
            programCategory,
            priority,
            location,
            circumstances);

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
