using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Channels;
using Common.Registration;
using RequestPortal.Application.Portal;

namespace RequestPortal.Web;

public sealed record RequestPortalFlowTelemetryItem(
    Guid RequestId,
    string Stage,
    DateTimeOffset ObservedAtUtc,
    string? Detail,
    bool Failed);

public sealed class RequestPortalOperationsFlowPublisher(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<RequestPortalOperationsFlowPublisher> logger)
    : BackgroundService, IRequestPortalOperationsFlowPublisher
{
    private const string ApplicationId = "RequestPortal";
    private readonly Channel<RequestPortalFlowTelemetryItem> queue =
        Channel.CreateBounded<RequestPortalFlowTelemetryItem>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    public bool TryEnqueue(
        Guid requestId,
        string stage,
        DateTimeOffset observedAtUtc,
        string? detail = null,
        bool failed = false)
        => queue.Writer.TryWrite(new(requestId, stage, observedAtUtc, detail, failed));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (RequestPortalFlowTelemetryItem item in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await PublishAsync(item, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Request Portal Operations flow publication failed; business workflow remains committed.");
            }
        }
    }

    private async Task PublishAsync(RequestPortalFlowTelemetryItem item, CancellationToken ct)
    {
        string? operationsUrl = configuration["Aegis:Operations:Url"];
        if (string.IsNullOrWhiteSpace(operationsUrl)) return;

        string instanceId = configuration["Service:Identity"]?.Trim() ?? Environment.MachineName;
        string identityFile = configuration["Aegis:Registration:IdentityFile"]
            ?? (OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Aegis", "RequestPortal", "registration-identity.json")
                : "/var/lib/aegis/requestportal/registration-identity.json");

        var identityStore = new FileRegistrationIdentityStore(identityFile);
        RegistrationIdentityDocument identity =
            await identityStore.LoadOrCreateAsync(ApplicationId, instanceId, ct);
        if (string.IsNullOrWhiteSpace(identity.Credential)) return;

        string[] stageNames =
        [
            "Applicant",
            "Validation",
            "Documents",
            "Submission",
            "Processing",
            "Downstream",
            "Acknowledgement"
        ];
        int currentIndex = Array.FindIndex(stageNames, x => x.Equals(item.Stage, StringComparison.OrdinalIgnoreCase));
        if (currentIndex < 0) currentIndex = 0;

        var stages = stageNames.Select((name, index) => new
        {
            name,
            state = index < currentIndex
                ? "Completed"
                : index == currentIndex
                    ? item.Failed ? "Failed" : item.Stage.Equals("Acknowledgement", StringComparison.OrdinalIgnoreCase) ? "Completed" : "Active"
                    : "Pending",
            startedAtUtc = index <= currentIndex ? item.ObservedAtUtc : (DateTimeOffset?)null,
            completedAtUtc = index < currentIndex || (index == currentIndex && item.Stage.Equals("Acknowledgement", StringComparison.OrdinalIgnoreCase))
                ? item.ObservedAtUtc
                : (DateTimeOffset?)null,
            warningAfterSeconds = index switch
            {
                0 => 30,
                1 => 10,
                2 => 30,
                3 => 10,
                4 => 60,
                5 => 60,
                _ => 30
            },
            criticalAfterSeconds = index switch
            {
                0 => 300,
                1 => 60,
                2 => 300,
                3 => 60,
                4 => 600,
                5 => 600,
                _ => 300
            },
            error = index == currentIndex && item.Failed ? item.Detail : null,
            parentStage = index == 0 ? null : stageNames[index - 1],
            branch = (string?)null
        }).ToArray();

        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            operationsUrl.TrimEnd('/') + "/api/operations/flows/observe")
        {
            Content = JsonContent.Create(new
            {
                correlationId = item.RequestId.ToString("D"),
                applicationId = ApplicationId,
                flowType = "RequestSubmission",
                instanceId,
                startedAtUtc = item.ObservedAtUtc,
                observedAtUtc = item.ObservedAtUtc,
                currentStage = item.Stage,
                relatedBusinessId = item.RequestId.ToString("D"),
                runbook = "Request Portal / Request Submission",
                stages
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.Credential);
        request.Headers.TryAddWithoutValidation("X-Aegis-Application-Id", identity.ApplicationId);
        request.Headers.TryAddWithoutValidation("X-Aegis-Instance-Id", identity.InstanceId);
        request.Headers.TryAddWithoutValidation("X-Aegis-Installation-Id", identity.InstallationId);
        request.Headers.TryAddWithoutValidation("X-Aegis-Correlation-Id", item.RequestId.ToString("N"));

        using HttpResponseMessage response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogDebug("Request Portal Operations flow publication returned HTTP {StatusCode}.", (int)response.StatusCode);
        }
    }
}
