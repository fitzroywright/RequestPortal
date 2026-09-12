using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Application.Integration;

public sealed record FfpManagerApplicationItem(
    Guid PortalItemId,
    string Description,
    decimal Quantity,
    string UnitOfMeasure);

public sealed record FfpManagerArtifactDescriptor(
    Guid PortalDocumentId,
    string DocumentType,
    string FileName,
    string ContentType,
    long SizeBytes,
    DateTimeOffset UploadedUtc);

public sealed record FfpManagerApplicationSubmission(
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
    IReadOnlyList<FfpManagerApplicationItem> Items,
    IReadOnlyList<FfpManagerArtifactDescriptor> Artifacts);

public sealed record FfpManagerApplicationReference(
    string ApplicationId,
    string? ApplicationNumber,
    DateTimeOffset AcceptedUtc,
    bool AlreadyExisted = false);

public sealed record FfpManagerApplicationStatus(
    string ApplicationId,
    string StatusCode,
    string StatusText,
    DateTimeOffset ChangedUtc,
    string? Detail = null,
    bool IsTerminal = false);

public sealed record FfpManagerInformationRequest(
    Guid MessageId,
    Guid PortalRequestId,
    string ApplicationId,
    string Message,
    DateTimeOffset RequestedUtc,
    string? RequestedBy = null,
    IReadOnlyList<string>? RequestedArtifacts = null,
    DateTimeOffset? DueUtc = null);

public sealed record FfpManagerInformationResponse(
    Guid MessageId,
    Guid PortalRequestId,
    string ApplicationId,
    Guid RespondedByUserId,
    string Message,
    DateTimeOffset RespondedUtc,
    IReadOnlyList<FfpManagerArtifactDescriptor> Artifacts);

public sealed record FfpManagerStatusCallback(
    Guid MessageId,
    Guid PortalRequestId,
    FfpManagerApplicationStatus Status);

public interface IFfpManagerBridgeClient
{
    Task<FfpManagerApplicationReference> SubmitApplicationAsync(
        FfpManagerApplicationSubmission submission,
        CancellationToken cancellationToken = default);

    Task UploadArtifactAsync(
        string applicationId,
        FfpManagerArtifactDescriptor descriptor,
        Stream content,
        CancellationToken cancellationToken = default);

    Task<FfpManagerApplicationStatus> GetStatusAsync(
        string applicationId,
        CancellationToken cancellationToken = default);

    Task SendInformationResponseAsync(
        FfpManagerInformationResponse response,
        CancellationToken cancellationToken = default);
}

public interface IFfpManagerIntegrationService
{
    Task<FfpManagerApplicationReference> SubmitRequestAsync(
        Guid requestId,
        CancellationToken cancellationToken = default);

    Task<FfpManagerApplicationStatus?> RefreshStatusAsync(
        Guid requestId,
        CancellationToken cancellationToken = default);

    Task ReceiveStatusAsync(
        Guid messageId,
        Guid requestId,
        FfpManagerApplicationStatus status,
        CancellationToken cancellationToken = default);

    Task ReceiveInformationRequestAsync(
        FfpManagerInformationRequest request,
        CancellationToken cancellationToken = default);

    Task RespondToInformationRequestAsync(
        Guid requestId,
        string applicationId,
        Guid userId,
        string message,
        IReadOnlyList<Guid> documentIds,
        CancellationToken cancellationToken = default);
}
