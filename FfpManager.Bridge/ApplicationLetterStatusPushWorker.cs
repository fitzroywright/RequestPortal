using System.Data;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;

namespace FfpManager.Bridge;

public sealed record ApplicationLetterSyncState(
    string ApplicationId
    , Guid PortalRequestId
    , FfpManagerApplicationStatus Status);

public interface IApplicationLetterSyncSource
{
    Task<IReadOnlyList<ApplicationLetterSyncState>> ReadAsync(
        CancellationToken cancellationToken = default);
}

public sealed class SqlApplicationLetterSyncSource : IApplicationLetterSyncSource
{
    private readonly BridgeOptions options;

    public SqlApplicationLetterSyncSource(IOptions<BridgeOptions> options)
    {
        this.options = options.Value;
    }

    public async Task<IReadOnlyList<ApplicationLetterSyncState>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.SqlConnectionString))
        {
            return [];
        }

        await using SqlConnection connection = new(options.SqlConnectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT
                al.ID
                , al.MessageID
                , al.DateCreated
                , al.DateUpdated
                , ast.Name
                , ast.Description
            FROM dbo.ApplicationLetters al
            LEFT JOIN dbo.ApplicationStatusTypes ast
                ON ast.ID = al.ApplicationStatus
            WHERE
                al.GCRecord IS NULL
                AND al.MessageID IS NOT NULL
            ORDER BY al.ID;
            """;

        await using SqlCommand command = new(sql, connection);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        List<ApplicationLetterSyncState> states = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            long applicationId = reader.GetInt64(0);
            Guid portalRequestId = reader.GetGuid(1);
            DateTime changed = !reader.IsDBNull(3)
                ? reader.GetDateTime(3)
                : !reader.IsDBNull(2)
                    ? reader.GetDateTime(2)
                    : DateTime.UtcNow;
            string rawStatus = reader.IsDBNull(4)
                ? "RECEIVED"
                : reader.GetString(4);
            string statusText = rawStatus;
            string canonical = MapStatus(rawStatus);
            string? detail = reader.IsDBNull(5)
                ? null
                : reader.GetString(5);

            bool terminal = canonical is "REJECTED"
                or "DECLINED"
                or "CLOSED";

            FfpManagerApplicationStatus status = new(
                applicationId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                , canonical
                , statusText
                , new DateTimeOffset(DateTime.SpecifyKind(changed, DateTimeKind.Utc))
                , detail
                , terminal);

            states.Add(new ApplicationLetterSyncState(
                status.ApplicationId
                , portalRequestId
                , status));
        }

        return states;
    }

    private string MapStatus(string rawStatus)
    {
        if (options.StatusMap.TryGetValue(rawStatus, out string? mapped) &&
            !string.IsNullOrWhiteSpace(mapped))
        {
            return Normalize(mapped);
        }

        return Normalize(rawStatus);
    }

    private static string Normalize(string value) =>
        value.Trim()
            .Replace('-', '_')
            .Replace(' ', '_')
            .ToUpperInvariant();
}

public sealed class ApplicationLetterStatusPushWorker : BackgroundService
{
    private readonly IApplicationLetterSyncSource source;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly IOptions<BridgeOptions> options;
    private readonly ILogger<ApplicationLetterStatusPushWorker> logger;
    private readonly Dictionary<string, Guid> delivered = new(StringComparer.OrdinalIgnoreCase);

    public ApplicationLetterStatusPushWorker(
        IApplicationLetterSyncSource source
        , IHttpClientFactory httpClientFactory
        , IOptions<BridgeOptions> options
        , ILogger<ApplicationLetterStatusPushWorker> logger)
    {
        this.source = source;
        this.httpClientFactory = httpClientFactory;
        this.options = options;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            BridgeOptions current = options.Value;
            if (current.EnableStatusPush &&
                current.Gateway.Equals("Sql", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await PushChangesAsync(current, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception
                        , "ApplicationLetter outbound synchronization cycle failed.");
                }
            }

            int seconds = Math.Clamp(current.StatusPushIntervalSeconds, 15, 3600);
            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
        }
    }

    private async Task PushChangesAsync(
        BridgeOptions current
        , CancellationToken cancellationToken)
    {
        IReadOnlyList<ApplicationLetterSyncState> states =
            await source.ReadAsync(cancellationToken);

        foreach (ApplicationLetterSyncState state in states)
        {
            Guid messageId = ApplicationLetterCallbackIdentity.CreateStatusMessageId(
                state.ApplicationId
                , state.Status.StatusCode
                , state.Status.ChangedUtc);

            if (delivered.TryGetValue(state.ApplicationId, out Guid previous) &&
                previous == messageId)
            {
                continue;
            }

            try
            {
                await PostStatusAsync(
                    current
                    , new FfpManagerStatusCallback(
                        messageId
                        , state.PortalRequestId
                        , state.Status)
                    , cancellationToken);

                delivered[state.ApplicationId] = messageId;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception
                    , "Unable to push ApplicationLetter {ApplicationId} status {StatusCode} to Request Portal."
                    , state.ApplicationId
                    , state.Status.StatusCode);
            }
        }
    }

    private async Task PostStatusAsync(
        BridgeOptions current
        , FfpManagerStatusCallback callback
        , CancellationToken cancellationToken)
    {
        HttpClient client = httpClientFactory.CreateClient("RequestPortalCallback");
        if (client.BaseAddress is null)
        {
            throw new InvalidOperationException(
                "Bridge:RequestPortalBaseUrl is not configured.");
        }

        using HttpRequestMessage request = new(
            HttpMethod.Post
            , "api/integration/ffpmanager/status")
        {
            Content = JsonContent.Create(callback)
        };

        if (!string.IsNullOrWhiteSpace(current.SharedSecret))
        {
            request.Headers.TryAddWithoutValidation(
                "X-FFP-Integration-Key"
                , current.SharedSecret);
        }

        using HttpResponseMessage response =
            await client.SendAsync(request, cancellationToken);

        response.EnsureSuccessStatusCode();
    }
}

public static class ApplicationLetterCallbackIdentity
{
    public static Guid CreateStatusMessageId(
        string applicationId
        , string statusCode
        , DateTimeOffset changedUtc)
    {
        string value = $"{applicationId}|{statusCode}|{changedUtc:O}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }
}
