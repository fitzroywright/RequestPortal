using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RequestPortal.Domain.Documents;
using RequestPortal.Domain.Requests;

namespace RequestPortal.Application.Administration;

public sealed record PendingOrganizationWorkItem(Guid OrganizationId, string OrganizationName, string CountryCode, DateTimeOffset CreatedUtc, int AgeDays);
public sealed record RequestWorkItem(Guid RequestId, string PublicReference, Guid OrganizationId, string OrganizationName, RequestStatus Status, DateTimeOffset? SubmittedUtc, DateTimeOffset CreatedUtc, int AgeDays);
public sealed record DocumentWorkItem(Guid DocumentId, Guid OrganizationId, string OrganizationName, DocumentType DocumentType, string OriginalFileName, DateTimeOffset UploadedUtc, int AgeDays);
public sealed record DeadLetterWorkItem(Guid MessageId, string MessageType, DateTimeOffset OccurredUtc, int AttemptCount, string? LastError);

public sealed record StaffOperationsSnapshot(
    IReadOnlyList<PendingOrganizationWorkItem> PendingOrganizations,
    IReadOnlyList<RequestWorkItem> OpenReviewRequests,
    IReadOnlyList<RequestWorkItem> ActionRequiredRequests,
    IReadOnlyList<DocumentWorkItem> DocumentsAwaitingVerification,
    IReadOnlyList<DeadLetterWorkItem> DeadLetters);

public interface IStaffOperationsService
{
    Task<StaffOperationsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task ReplayDeadLetterAsync(Guid staffUserId, Guid messageId, CancellationToken cancellationToken = default);
}
