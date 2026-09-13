using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RequestPortal.Domain.Documents;
using RequestPortal.Domain.Notifications;
using RequestPortal.Domain.Requests;

namespace RequestPortal.Application.Administration;

public sealed record PendingOrganizationWorkItem(Guid OrganizationId, string OrganizationName, string CountryCode, DateTimeOffset CreatedUtc, int AgeDays);
public sealed record OrganizationReverificationWorkItem(Guid OrganizationId, string OrganizationName, string CountryCode, DateOnly ExpiryDate, string Status, int DaysUntilExpiry);
public sealed record RequestWorkItem(Guid RequestId, string PublicReference, Guid OrganizationId, string OrganizationName, RequestStatus Status, DateTimeOffset? SubmittedUtc, DateTimeOffset CreatedUtc, int AgeDays);
public sealed record DocumentWorkItem(Guid DocumentId, Guid OrganizationId, string OrganizationName, DocumentType DocumentType, string OriginalFileName, DateTimeOffset UploadedUtc, int AgeDays);
public sealed record DeadLetterWorkItem(Guid MessageId, string MessageType, DateTimeOffset OccurredUtc, int AttemptCount, string? LastError);
public sealed record NotificationDeliveryWorkItem(Guid DeliveryId, NotificationChannel Channel, string Destination, string Subject, NotificationDeliveryStatus Status, DateTimeOffset CreatedUtc, string? LastError);

public sealed record StaffOperationsSnapshot(
    IReadOnlyList<PendingOrganizationWorkItem> PendingOrganizations,
    IReadOnlyList<OrganizationReverificationWorkItem> ReverificationOrganizations,
    IReadOnlyList<RequestWorkItem> OpenReviewRequests,
    IReadOnlyList<RequestWorkItem> ActionRequiredRequests,
    IReadOnlyList<DocumentWorkItem> DocumentsAwaitingVerification,
    IReadOnlyList<DeadLetterWorkItem> DeadLetters,
    IReadOnlyList<NotificationDeliveryWorkItem> FailedNotificationDeliveries);

public interface IStaffOperationsService
{
    Task<StaffOperationsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task ReplayDeadLetterAsync(Guid staffUserId, Guid messageId, CancellationToken cancellationToken = default);
}
