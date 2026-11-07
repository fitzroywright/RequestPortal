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
    IReadOnlyList<ExternalArtifactDescriptor> Artifacts,
    string? Parish = null,
    string DocumentSource = "Request Portal",
    string? FirstName = null,
    string? LastName = null,
    string? ApplicantAddress = null,
    string? ApplicantEmail = null,
    string? ApplicantGender = null,
    string? ApplicantEmploymentStatus = null,
    string? StreetAddress = null,
    string? CommunityDistrict = null,
    string? PrimaryTelephone = null,
    string? SecondaryTelephone = null,
    DateOnly? DateOfBirth = null,
    decimal? WeeklyIncome = null,
    string? ApplicantMaritalStatus = null,
    bool? HasChildrenUnder18LivingFullTime = null,
    bool? HasDisabilityOrSeriousMedicalCondition = null,
    IReadOnlyList<string>? CurrentAssistancePrograms = null,
    IReadOnlyList<string>? VictimCircumstances = null,
    int? PayFrequency = null,
    int? PhoneLineType = null);

public sealed record ExternalApplicationReference(string ApplicationId, string? ApplicationNumber, DateTimeOffset AcceptedUtc, bool AlreadyExisted = false);
public sealed record ExternalApplicationReceipt(Guid PortalRequestId, string ApplicationId, string ApplicationNumber,
    DateTimeOffset AcceptedUtc, string Server, string Database);
public sealed record ExternalReturnOutboxStatus(int Pending, int Failed, int Locked, DateTimeOffset? OldestPendingUtc);
public sealed record ExternalApplicationStatus(string ApplicationId, string StatusCode, string StatusText, DateTimeOffset ChangedUtc, string? Detail = null, bool IsTerminal = false);
public sealed record ExternalInformationRequest(Guid MessageId, Guid PortalRequestId, string ApplicationId, string Message, DateTimeOffset RequestedUtc, string? RequestedBy = null, IReadOnlyList<string>? RequestedArtifacts = null, DateTimeOffset? DueUtc = null);
public sealed record ExternalInformationResponse(Guid MessageId, Guid PortalRequestId, string ApplicationId, Guid RespondedByUserId, string Message, DateTimeOffset RespondedUtc, IReadOnlyList<ExternalArtifactDescriptor> Artifacts);
public sealed record ExternalStatusCallback(Guid MessageId, Guid PortalRequestId, ExternalApplicationStatus Status);

public interface IExternalApplicationBridgeClient
{
    Task<ApplicationFieldSnapshot> ReadFieldsAsync(Guid requestId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The bridge does not support field previews.");
    Task<ApplicationFieldSnapshot> UpdateFieldsAsync(ApplicationFieldUpdate update, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The bridge does not support field updates.");
    Task<ExternalApplicationReceipt?> FindApplicationAsync(Guid requestId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The bridge must support verified lookup by portal request ID before transfers can run.");
    Task<ExternalReturnOutboxStatus> GetReturnOutboxAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The bridge does not expose return-event diagnostics.");
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
