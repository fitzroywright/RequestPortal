using System;
using System.Threading;
using System.Threading.Tasks;
using RequestPortal.Domain.Requests;

namespace RequestPortal.Application.Requests;

public sealed record EditableRequestDraft(
    Guid Id,
    string PublicReference,
    RequestStatus Status,
    RequestKind Kind,
    string Summary,
    string? Category,
    string? Subcategory,
    string? ProgramCategory,
    RequestPriority Priority,
    string? Location,
    string? Circumstances);

public interface IRequestDraftService
{
    Task<EditableRequestDraft?> GetEditableAsync(
        Guid userId,
        Guid requestId,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
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
        CancellationToken cancellationToken = default);

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
