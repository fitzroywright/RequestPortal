using System.Collections.Concurrent;
using RequestPortal.Application.Integration;

namespace FfpManager.Bridge;

public interface IFfpManagerGateway
{
    Task<ExternalApplicationReceipt?> FindApplicationAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task<ExternalApplicationReference> CreateApplicationAsync(
        ExternalApplicationSubmission submission,
        CancellationToken cancellationToken = default);

    Task SaveArtifactAsync(
        string applicationId,
        ExternalArtifactDescriptor descriptor,
        Stream content,
        CancellationToken cancellationToken = default);

    Task<ExternalApplicationStatus?> GetStatusAsync(
        string applicationId,
        CancellationToken cancellationToken = default);

    Task SaveInformationResponseAsync(
        ExternalInformationResponse response,
        CancellationToken cancellationToken = default);

    Task<ExternalApplicationSubmission?> GetApplicationAsync(
        string applicationId,
        CancellationToken cancellationToken = default);

    Task<ExternalApplicationStatus> SetStatusAsync(
        string applicationId,
        string statusCode,
        string statusText,
        string? detail,
        bool terminal,
        CancellationToken cancellationToken = default);
}

public sealed record FakeFfpManagerApplicationSnapshot(
    ExternalApplicationSubmission Submission,
    ExternalApplicationReference Reference,
    ExternalApplicationStatus Status,
    IReadOnlyList<ExternalArtifactDescriptor> Artifacts,
    IReadOnlyList<ExternalInformationResponse> Responses);

public sealed class FakeFfpManagerGateway : IFfpManagerGateway
{
    public Task<ExternalApplicationReceipt?> FindApplicationAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        var found = byApplicationId.Values.SingleOrDefault(item => item.Submission.PortalRequestId == requestId);
        return Task.FromResult(found is null ? null : new ExternalApplicationReceipt(requestId,
            found.Reference.ApplicationId, found.Reference.ApplicationNumber!, found.Reference.AcceptedUtc, "Fake", "Fake"));
    }
    private sealed class FakeApplication
    {
        public FakeApplication(
            ExternalApplicationSubmission submission,
            ExternalApplicationReference reference)
        {
            Submission = submission;
            Reference = reference;
            Status = new ExternalApplicationStatus(
                reference.ApplicationId,
                "RECEIVED",
                "Received by FFP Manager",
                reference.AcceptedUtc);
        }

        public ExternalApplicationSubmission Submission { get; }
        public ExternalApplicationReference Reference { get; }
        public ConcurrentDictionary<Guid, ExternalArtifactDescriptor> Artifacts { get; } = new();
        public ConcurrentBag<ExternalInformationResponse> Responses { get; } = new();
        public ExternalApplicationStatus Status { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, FakeApplication> byRequest = new();
    private readonly ConcurrentDictionary<string, FakeApplication> byApplicationId = new(StringComparer.OrdinalIgnoreCase);
    private long sequence = 1000;

    public Task<ExternalApplicationReference> CreateApplicationAsync(
        ExternalApplicationSubmission submission,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (byRequest.TryGetValue(submission.PortalRequestId, out FakeApplication? existing))
        {
            return Task.FromResult(existing.Reference with { AlreadyExisted = true });
        }

        long number = Interlocked.Increment(ref sequence);
        string applicationId = $"APP-{number}";
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ExternalApplicationReference reference = new(applicationId, applicationId, now);
        FakeApplication created = new(submission, reference);

        FakeApplication actual = byRequest.GetOrAdd(submission.PortalRequestId, created);
        byApplicationId.TryAdd(actual.Reference.ApplicationId, actual);
        return Task.FromResult(actual.Reference with { AlreadyExisted = !ReferenceEquals(actual, created) });
    }

    public async Task SaveArtifactAsync(
        string applicationId,
        ExternalArtifactDescriptor descriptor,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        FakeApplication application = GetRequired(applicationId);
        await content.CopyToAsync(Stream.Null, cancellationToken);
        application.Artifacts[descriptor.PortalDocumentId] = descriptor;
    }

    public Task<ExternalApplicationStatus?> GetStatusAsync(
        string applicationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            byApplicationId.TryGetValue(applicationId, out FakeApplication? application)
                ? application.Status
                : null);
    }

    public Task SaveInformationResponseAsync(
        ExternalInformationResponse response,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetRequired(response.ApplicationId).Responses.Add(response);
        return Task.CompletedTask;
    }

    public Task<ExternalApplicationSubmission?> GetApplicationAsync(
        string applicationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            byApplicationId.TryGetValue(applicationId, out FakeApplication? application)
                ? application.Submission
                : null);
    }

    public Task<ExternalApplicationStatus> SetStatusAsync(
        string applicationId,
        string statusCode,
        string statusText,
        string? detail,
        bool terminal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FakeApplication application = GetRequired(applicationId);
        application.Status = new ExternalApplicationStatus(
            applicationId,
            statusCode,
            statusText,
            DateTimeOffset.UtcNow,
            detail,
            terminal);
        return Task.FromResult(application.Status);
    }

    public FakeFfpManagerApplicationSnapshot GetSnapshot(string applicationId)
    {
        FakeApplication application = GetRequired(applicationId);
        return new FakeFfpManagerApplicationSnapshot(
            application.Submission,
            application.Reference,
            application.Status,
            application.Artifacts.Values.OrderBy(item => item.UploadedUtc).ToArray(),
            application.Responses.OrderBy(item => item.RespondedUtc).ToArray());
    }

    private FakeApplication GetRequired(string applicationId) =>
        byApplicationId.TryGetValue(applicationId, out FakeApplication? application)
            ? application
            : throw new KeyNotFoundException($"FFP Manager application '{applicationId}' was not found.");
}
