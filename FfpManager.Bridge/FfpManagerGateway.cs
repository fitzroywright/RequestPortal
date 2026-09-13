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

public sealed record FakeFfpManagerApplicationSnapshot(
    FfpManagerApplicationSubmission Submission,
    FfpManagerApplicationReference Reference,
    FfpManagerApplicationStatus Status,
    IReadOnlyList<FfpManagerArtifactDescriptor> Artifacts,
    IReadOnlyList<FfpManagerInformationResponse> Responses);

public sealed class FakeFfpManagerGateway : IFfpManagerGateway
{
    private sealed class FakeApplication
    {
        public FakeApplication(
            FfpManagerApplicationSubmission submission,
            FfpManagerApplicationReference reference)
        {
            Submission = submission;
            Reference = reference;
            Status = new FfpManagerApplicationStatus(
                reference.ApplicationId,
                "RECEIVED",
                "Received by FFP Manager",
                reference.AcceptedUtc);
        }

        public FfpManagerApplicationSubmission Submission { get; }
        public FfpManagerApplicationReference Reference { get; }
        public ConcurrentDictionary<Guid, FfpManagerArtifactDescriptor> Artifacts { get; } = new();
        public ConcurrentBag<FfpManagerInformationResponse> Responses { get; } = new();
        public FfpManagerApplicationStatus Status { get; set; }
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
        FakeApplication created = new(submission, reference);

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
