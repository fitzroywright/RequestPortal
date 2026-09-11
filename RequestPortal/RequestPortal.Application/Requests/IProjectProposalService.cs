using System;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Application.Requests;

public sealed record ProjectProposalDetail(
    Guid RequestId,
    string PublicReference,
    string Title,
    string RichContentHtml,
    int Version,
    DateTimeOffset UpdatedUtc,
    bool CanEdit);

public interface IProjectProposalService
{
    Task InitializeProjectAsync(Guid userId, Guid requestId, CancellationToken cancellationToken = default);

    Task<ProjectProposalDetail?> GetAsync(Guid userId, Guid requestId, bool isStaff, CancellationToken cancellationToken = default);

    Task SaveAsync(
        Guid userId,
        Guid requestId,
        string title,
        string richContentHtml,
        CancellationToken cancellationToken = default);
}
