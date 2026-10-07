using System;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Application.Requests;

public sealed record QuickIntakeSubmission(
    string FirstName,
    string LastName,
    string MobileNumber,
    string Parish,
    string? Town,
    string? Category,
    string AssistanceNeeded);

public sealed record QuickIntakeResult(
    Guid RequestId,
    string PublicReference,
    string MobileNumber);

public sealed record QuickIntakeView(
    Guid RequestId,
    string FirstName,
    string LastName,
    string MobileNumber,
    string Parish,
    string? Town,
    DateTimeOffset CreatedUtc,
    Guid? ConvertedToUserId,
    DateTimeOffset? ConvertedUtc)
{
    public bool IsConverted => ConvertedToUserId.HasValue;
}

public interface IQuickIntakeService
{
    Task<QuickIntakeView?> GetAsync(
        Guid requestId,
        CancellationToken cancellationToken = default);

    Task<QuickIntakeResult> CreateAsync(
        QuickIntakeSubmission submission,
        CancellationToken cancellationToken = default);

    Task ConvertToIndividualAsync(
        Guid requestId,
        Guid userId,
        Guid staffUserId,
        CancellationToken cancellationToken = default);
}
