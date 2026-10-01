using System;
using System.Threading;
using System.Threading.Tasks;
using RequestPortal.Domain.Requests;

namespace RequestPortal.Application.Requests;

public interface IRequestDraftService
{
    Task SetDetailsAsync(
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
        CancellationToken cancellationToken = default);
}
