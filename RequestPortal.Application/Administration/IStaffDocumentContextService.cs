using RequestPortal.Domain.Documents;

namespace RequestPortal.Application.Administration;

public enum StaffDocumentContextStatus { Available, NotFound, Archived }

public interface IStaffDocumentContextService
{
    Task<StaffDocumentContextStatus> ValidateAsync(DocumentOwnerType ownerType, Guid ownerId, Guid documentId, CancellationToken cancellationToken = default);
}
