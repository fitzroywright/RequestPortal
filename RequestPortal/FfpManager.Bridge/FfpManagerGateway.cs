using System.Collections.Concurrent;
using RequestPortal.Application.Integration;

namespace FfpManager.Bridge;

public interface IFfpManagerGateway
{
    Task<FfpManagerApplicationReference> CreateApplicationAsync(
        FfpManagerApplicationSubmission submission,
        CancellationToken cancellationToken = default);

    Task SaveArtifactAsync(
        string applicationId,
        FfpManagerArtifactDescriptor descriptor,
        Stream content,
        CancellationToken cancellationToken = default);

    Task<FfpManagerApplicationStatus?> GetStatusAsync(
        string applicationId,
        CancellationToken cancellationToken = default);

    Task SaveInformationResponseAsync(
        FfpManagerInformationResponse response,
        CancellationToken cancellationToken = default);

    Task<FfpManagerApplicationSubmission?> GetApplicationAsync(
        string applicationId,
        CancellationToken cancellationToken = default);

    Task<FfpManagerApplicationStatus> SetStatusAsync(
        string applicationId,
        string statusCode,
        string statusText,
        string? detail,
        bool terminal,
        CancellationToken cancellationToken = default);
}

public sealed class FakeFfpManagerGateway : IFfpManagerGateway
{
    private sealed record FakeApplication(
        FfpManagerApplicationSubmission Submission,
        FfpManagerApplicationReference Reference,
        ConcurrentDictionary<Guid, FfpManagerArtifactDescriptor> Artifacts,
        ConcurrentBag<FfpManagerInformationResponse> Responses)
    {
        public FfpManagerApplicationStatus Status { get; set; } = new(
            Reference.ApplicationId,
            "RECEIVED",
            "Received by FFP Manager",
            Reference.AcceptedUtc);
    }

    private readonly ConcurrentDictionary<Guid, FakeApplication> byCorrelation = new();
    private readonly ConcurrentDictionary<string, FakeApplication> byApplicationId = new(StringComparer.OrdinalIgnoreCase);
    private long sequence = 1000;

    public Task<FfpManagerApplicationReference> CreateApplicationAsync(
        FfpManagerApplicationSubmission submission,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (byCorrelation.TryGetValue(submission.CorrelationId, out FakeApplication? existing))
        {
            return Task.FromResult(existing.Reference with { AlreadyExisted = true });
        }

        long number = Interlocked.Increment(ref sequence);
        string applicationId = $"APP-{number}";
        DateTimeOffset now = DateTimeOffset.UtcNow;
        FfpManagerApplicationReference reference = new(applicationId, applicationId, now);
        FakeApplication created = new(
            submission,
            reference,
            new ConcurrentDictionary<Guid, FfpManagerArtifactDescriptor>(),
            new ConcurrentBag<FfpManagerInformationResponse>());

        FakeApplication actual = byCorrelation.GetOrAdd(submission.CorrelationId, created);
        byApplicationId.TryAdd(actual.Reference.ApplicationId, actual);
        return Task.FromResult(actual.Reference with { AlreadyExisted = !ReferenceEquals(actual, created) });
    }

    public async Task SaveArtifactAsync(
        string applicationId,
        FfpManagerArtifactDescriptor descriptor,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        FakeApplication application = GetRequired(applicationId);
        await content.CopyToAsync(Stream.Null, cancellationToken);
        application.Artifacts[descriptor.PortalDocumentId] = descriptor;
    }

    public Task<FfpManagerApplicationStatus?> GetStatusAsync(
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
        FfpManagerInformationResponse response,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetRequired(response.ApplicationId).Responses.Add(response);
        return Task.CompletedTask;
    }

    public Task<FfpManagerApplicationSubmission?> GetApplicationAsync(
        string applicationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            byApplicationId.TryGetValue(applicationId, out FakeApplication? application)
                ? application.Submission
                : null);
    }

    public Task<FfpManagerApplicationStatus> SetStatusAsync(
        string applicationId,
        string statusCode,
        string statusText,
        string? detail,
        bool terminal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FakeApplication application = GetRequired(applicationId);
        application.Status = new FfpManagerApplicationStatus(
            applicationId,
            statusCode,
            statusText,
            DateTimeOffset.UtcNow,
            detail,
            terminal);
        return Task.FromResult(application.Status);
    }

    private FakeApplication GetRequired(string applicationId) =>
        byApplicationId.TryGetValue(applicationId, out FakeApplication? application)
            ? application
            : throw new KeyNotFoundException($"FFP Manager application '{applicationId}' was not found.");
}

public sealed class SqlFfpManagerGateway : IFfpManagerGateway
{
    private static InvalidOperationException SchemaRequired() => new(
        "The FFP Manager SQL gateway cannot be enabled until the Application Letter, Items Requested, Supporting Documents, workflow/status, and correspondence schemas have been mapped.");

    public Task<FfpManagerApplicationReference> CreateApplicationAsync(FfpManagerApplicationSubmission submission, CancellationToken cancellationToken = default) => throw SchemaRequired();
    public Task SaveArtifactAsync(string applicationId, FfpManagerArtifactDescriptor descriptor, Stream content, CancellationToken cancellationToken = default) => throw SchemaRequired();
    public Task<FfpManagerApplicationStatus?> GetStatusAsync(string applicationId, CancellationToken cancellationToken = default) => throw SchemaRequired();
    public Task SaveInformationResponseAsync(FfpManagerInformationResponse response, CancellationToken cancellationToken = default) => throw SchemaRequired();
    public Task<FfpManagerApplicationSubmission?> GetApplicationAsync(string applicationId, CancellationToken cancellationToken = default) => throw SchemaRequired();
    public Task<FfpManagerApplicationStatus> SetStatusAsync(string applicationId, string statusCode, string statusText, string? detail, bool terminal, CancellationToken cancellationToken = default) => throw SchemaRequired();
}
