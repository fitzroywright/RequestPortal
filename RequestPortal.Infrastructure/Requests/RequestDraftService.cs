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
    public async Task<EditableRequestDraft?> GetEditableAsync(
        Guid userId,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        AssistanceRequest? request = await dbContext.AssistanceRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == requestId, cancellationToken);

        if (request is null)
        {
            return null;
        }

        bool allowed = await organizationAccessService.IsApprovedMemberAsync(userId, request.OrganizationId, cancellationToken);
        if (!allowed)
        {
            throw new UnauthorizedAccessException("The current user cannot edit this request.");
        }

        if (request.Status is not (RequestStatus.Draft or RequestStatus.MoreInformationRequired))
        {
            throw new InvalidOperationException("This request can no longer be edited.");
        }

        return new EditableRequestDraft(
            request.Id,
            request.PublicReference,
            request.Status,
            request.Kind,
            request.Summary ?? string.Empty,
            request.Category,
            request.Subcategory,
            request.ProgramCategory,
            request.Priority,
            request.Location,
            request.Circumstances,
            await dbContext.AssistanceRequestItems
                .AsNoTracking()
                .Where(item => item.RequestId == request.Id)
                .OrderBy(item => item.ItemName)
                .Select(item => new EditableRequestItem(
                    item.Id,
                    item.ItemName,
                    item.Description,
                    item.Quantity,
                    item.UnitOfMeasureId,
                    item.Notes))
                .ToListAsync(cancellationToken));
    }

    public async Task UpdateAsync(
        Guid userId,
        Guid requestId,
        RequestKind kind,
        string summary,
        string? category,
        string? subcategory,
        string? programCategory,
        RequestPriority priority,
        string? location,
        string? circumstances,
        IReadOnlyList<EditableRequestItem> items,
        CancellationToken cancellationToken = default)
    {
        AssistanceRequest request = await dbContext.AssistanceRequests
            .SingleAsync(item => item.Id == requestId, cancellationToken);

        bool allowed = await organizationAccessService.IsApprovedMemberAsync(userId, request.OrganizationId, cancellationToken);
        if (!allowed)
        {
            throw new UnauthorizedAccessException("The current user cannot edit this request.");
        }

        request.UpdateEditableDetails(
            kind,
            summary,
            category,
            subcategory,
            programCategory,
            priority,
            location,
            circumstances);

        if (kind == RequestKind.GoodsAndServices)
        {
            if (items.Count == 0)
            {
                throw new InvalidOperationException("A goods or services request must contain at least one requested item.");
            }

            AssistanceRequestItem[] existingItems = await dbContext.AssistanceRequestItems
                .Where(item => item.RequestId == requestId)
                .ToArrayAsync(cancellationToken);

            dbContext.AssistanceRequestItems.RemoveRange(existingItems);

            foreach (EditableRequestItem item in items)
            {
                dbContext.AssistanceRequestItems.Add(new AssistanceRequestItem(
                    item.Id == Guid.Empty ? Guid.NewGuid() : item.Id,
                    requestId,
                    item.ItemName,
                    item.Description,
                    item.Quantity,
                    item.UnitOfMeasureId,
                    item.Notes));
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

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
