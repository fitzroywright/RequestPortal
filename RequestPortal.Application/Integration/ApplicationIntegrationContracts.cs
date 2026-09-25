using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Application.Integration;

public sealed record ExternalApplicationItem(Guid PortalItemId, string Description, decimal Quantity, string UnitOfMeasure);
public sealed record ExternalArtifactDescriptor(Guid PortalDocumentId, string DocumentType, string FileName, string ContentType, long SizeBytes, DateTimeOffset UploadedUtc);

public sealed record ExternalApplicationSubmission(
    Guid CorrelationId,
    Guid PortalRequestId,
    string PublicReference,
    Guid OrganizationId,
    string OrganizationName,
    string? OrganizationRegistrationNumber,
    string? OrganizationAddress,
    string? OrganizationPhone,
    string? OrganizationEmail,
    Guid SubmittedByUserId,
    DateTimeOffset SubmittedUtc,
    string RequestKind,
    string Priority,
    string? Summary,
    string? Category,
    string? Subcategory,
    string? ProgramCategory,
    string? Location,
    string? Circumstances,
    IReadOnlyList<ExternalApplicationItem> Items,
    IReadOnlyList<ExternalArtifactDescriptor> Artifacts);

public sealed record ExternalApplicationReference(string ApplicationId, string? ApplicationNumber, DateTimeOffset AcceptedUtc, bool AlreadyExisted = false);
public sealed record ExternalApplicationStatus(string ApplicationId, string StatusCode, string StatusText, DateTimeOffset ChangedUtc, string? Detail = null, bool IsTerminal = false);
public sealed record ExternalInformationRequest(Guid MessageId, Guid PortalRequestId, string ApplicationId, string Message, DateTimeOffset RequestedUtc, string? RequestedBy = null, IReadOnlyList<string>? RequestedArtifacts = null, DateTimeOffset? DueUtc = null);
public sealed record ExternalInformationResponse(Guid MessageId, Guid PortalRequestId, string ApplicationId, Guid RespondedByUserId, string Message, DateTimeOffset RespondedUtc, IReadOnlyList<ExternalArtifactDescriptor> Artifacts);
public sealed record ExternalStatusCallback(Guid MessageId, Guid PortalRequestId, ExternalApplicationStatus Status);

public interface IExternalApplicationBridgeClient
{
    Task<ExternalApplicationReference> SubmitApplicationAsync(ExternalApplicationSubmission submission, CancellationToken cancellationToken = default);
    Task UploadArtifactAsync(string applicationId, ExternalArtifactDescriptor descriptor, Stream content, CancellationToken cancellationToken = default);
    Task<ExternalApplicationStatus> GetStatusAsync(string applicationId, CancellationToken cancellationToken = default);
    Task SendInformationResponseAsync(ExternalInformationResponse response, CancellationToken cancellationToken = default);
}

public interface IExternalApplicationIntegrationService
{
    Task<ExternalApplicationReference> SubmitRequestAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task<ExternalApplicationStatus?> RefreshStatusAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task ReceiveStatusAsync(Guid messageId, Guid requestId, ExternalApplicationStatus status, CancellationToken cancellationToken = default);
    Task ReceiveInformationRequestAsync(ExternalInformationRequest request, CancellationToken cancellationToken = default);
    Task RespondToInformationRequestAsync(Guid requestId, string applicationId, Guid userId, string message, IReadOnlyList<Guid> documentIds, CancellationToken cancellationToken = default);
}
