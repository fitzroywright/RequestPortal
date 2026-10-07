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

public interface IQuickIntakeService
{
    Task<QuickIntakeResult> CreateAsync(
        QuickIntakeSubmission submission,
        CancellationToken cancellationToken = default);

    Task ConvertToIndividualAsync(
        Guid requestId,
        Guid userId,
        Guid staffUserId,
        CancellationToken cancellationToken = default);
}
