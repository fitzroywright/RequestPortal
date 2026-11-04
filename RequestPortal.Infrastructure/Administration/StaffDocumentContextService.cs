using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Administration;
using RequestPortal.Domain.Documents;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Administration;

/// <summary>Validates polymorphic ownership and its organization before staff navigation or actions.</summary>
public sealed class StaffDocumentContextService(RequestPortalDbContext db) : IStaffDocumentContextService
{
    public async Task<StaffDocumentContextStatus> ValidateAsync(DocumentOwnerType ownerType, Guid ownerId, Guid documentId, CancellationToken cancellationToken = default)
    {
        var document = await db.Documents.AsNoTracking().Where(d => d.Id == documentId && d.OwnerType == ownerType && d.OwnerId == ownerId)
            .Select(d => new { d.OrganizationId, d.IsArchived }).SingleOrDefaultAsync(cancellationToken);
        if (document is null) return StaffDocumentContextStatus.NotFound;
        bool valid = ownerType switch
        {
            DocumentOwnerType.Organization => ownerId == document.OrganizationId && await db.Organizations.AnyAsync(o => o.Id == ownerId, cancellationToken),
            DocumentOwnerType.AssistanceRequest => await db.AssistanceRequests.AnyAsync(r => r.Id == ownerId && r.OrganizationId == document.OrganizationId, cancellationToken),
            _ => false
        };
        if (!valid) return StaffDocumentContextStatus.NotFound;
        return document.IsArchived ? StaffDocumentContextStatus.Archived : StaffDocumentContextStatus.Available;
    }
}
