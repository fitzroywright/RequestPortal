using System.Data;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;

namespace FfpManager.Bridge;

public sealed record IntegrationOutboxMessage(
    long SequenceNo
    , Guid EventId
    , string EventType
    , string ApplicationId
    , Guid PortalRequestId
    , string? StatusCode
    , string? StatusText
    , string? Detail
    , string? Message
    , string? RequestedBy
    , DateTimeOffset? DueUtc
    , DateTimeOffset OccurredUtc
    , int AttemptCount);

public sealed record IntegrationOutboxSnapshot(
    int Pending
    , int Failed
    , int Locked
    , DateTimeOffset? OldestPendingUtc);

public interface IIntegrationOutboxStore
{
    Task<IntegrationOutboxMessage?> ClaimNextAsync(CancellationToken cancellationToken = default);

    Task MarkDeliveredAsync(
        long sequenceNo
        , CancellationToken cancellationToken = default);

    Task MarkFailedAsync(
        long sequenceNo
        , int attemptCount
        , string error
        , CancellationToken cancellationToken = default);

    Task<IntegrationOutboxSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default);

    Task<bool> SchemaExistsAsync(
        CancellationToken cancellationToken = default);
}

public sealed class SqlIntegrationOutboxStore : IIntegrationOutboxStore
{
    private readonly BridgeOptions options;

    public SqlIntegrationOutboxStore(IOptions<BridgeOptions> options)
    {
        this.options = options.Value;
    }

    public async Task<IntegrationOutboxMessage?> ClaimNextAsync(
        CancellationToken cancellationToken = default)
    {
        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken);
        await using SqlTransaction transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted
                , cancellationToken);

        const string sql = """
            ;WITH candidate AS
            (
                SELECT TOP (1) *
                FROM dbo.RequestPortalIntegrationOutbox WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE
                    DeliveredUtc IS NULL
                    AND AttemptCount < @MaxAttempts
                    AND (NextAttemptUtc IS NULL OR NextAttemptUtc <= SYSUTCDATETIME())
                    AND (LockedUntilUtc IS NULL OR LockedUntilUtc <= SYSUTCDATETIME())
                ORDER BY SequenceNo
            )
            UPDATE candidate
            SET
                LockedUntilUtc = DATEADD(second, @LockSeconds, SYSUTCDATETIME()),
                AttemptCount = AttemptCount + 1
            OUTPUT
                inserted.SequenceNo,
                inserted.EventId,
                inserted.EventType,
                inserted.ApplicationLetterId,
                inserted.PortalRequestId,
                inserted.StatusCode,
                inserted.StatusText,
                inserted.Detail,
                inserted.Message,
                inserted.RequestedBy,
                inserted.DueUtc,
                inserted.OccurredUtc,
                inserted.AttemptCount;
            """;

        await using SqlCommand command = new(sql, connection, transaction);
        command.Parameters.Add("@MaxAttempts", SqlDbType.Int).Value =
            Math.Clamp(options.OutboxMaxAttempts, 1, 100);
        command.Parameters.Add("@LockSeconds", SqlDbType.Int).Value =
            Math.Clamp(options.OutboxLockSeconds, 15, 3600);

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            // SQL Server requires the reader to be closed before committing,
            // including when the outbox is empty.
            await reader.CloseAsync();
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        IntegrationOutboxMessage message = new(
            reader.GetInt64(0)
            , reader.GetGuid(1)
            , reader.GetString(2)
            , reader.GetInt64(3).ToString(System.Globalization.CultureInfo.InvariantCulture)
            , reader.GetGuid(4)
            , reader.IsDBNull(5) ? null : reader.GetString(5)
            , reader.IsDBNull(6) ? null : reader.GetString(6)
            , reader.IsDBNull(7) ? null : reader.GetString(7)
            , reader.IsDBNull(8) ? null : reader.GetString(8)
            , reader.IsDBNull(9) ? null : reader.GetString(9)
            , reader.IsDBNull(10)
                ? null
                : new DateTimeOffset(
                    DateTime.SpecifyKind(reader.GetDateTime(10), DateTimeKind.Utc))
            , new DateTimeOffset(
                DateTime.SpecifyKind(reader.GetDateTime(11), DateTimeKind.Utc))
            , reader.GetInt32(12));

        await reader.CloseAsync();
        await transaction.CommitAsync(cancellationToken);
        return message;
    }

    public async Task MarkDeliveredAsync(
        long sequenceNo
        , CancellationToken cancellationToken = default)
    {
        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            UPDATE dbo.RequestPortalIntegrationOutbox
            SET
                DeliveredUtc = SYSUTCDATETIME(),
                LockedUntilUtc = NULL,
                NextAttemptUtc = NULL,
                LastError = NULL
            WHERE SequenceNo = @SequenceNo;
            """;
        await using SqlCommand command = new(sql, connection);
        command.Parameters.Add("@SequenceNo", SqlDbType.BigInt).Value = sequenceNo;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(
        long sequenceNo
        , int attemptCount
        , string error
        , CancellationToken cancellationToken = default)
    {
        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken);
        int delaySeconds = CalculateRetryDelaySeconds(attemptCount);
        const string sql = """
            UPDATE dbo.RequestPortalIntegrationOutbox
            SET
                LockedUntilUtc = NULL,
                NextAttemptUtc = DATEADD(second, @DelaySeconds, SYSUTCDATETIME()),
                LastError = @LastError
            WHERE SequenceNo = @SequenceNo;
            """;
        await using SqlCommand command = new(sql, connection);
        command.Parameters.Add("@SequenceNo", SqlDbType.BigInt).Value = sequenceNo;
        command.Parameters.Add("@DelaySeconds", SqlDbType.Int).Value = delaySeconds;
        SqlParameter errorParameter = command.Parameters.Add(
            "@LastError"
            , SqlDbType.NVarChar
            , 2000);
        errorParameter.Value = Truncate(error, 2000);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IntegrationOutboxSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT
                SUM(CASE
                    WHEN DeliveredUtc IS NULL
                         AND AttemptCount < @MaxAttempts
                    THEN 1 ELSE 0 END) AS Pending,
                SUM(CASE
                    WHEN DeliveredUtc IS NULL
                         AND AttemptCount >= @MaxAttempts
                    THEN 1 ELSE 0 END) AS Failed,
                SUM(CASE
                    WHEN DeliveredUtc IS NULL
                         AND LockedUntilUtc > SYSUTCDATETIME()
                    THEN 1 ELSE 0 END) AS Locked,
                MIN(CASE
                    WHEN DeliveredUtc IS NULL
                    THEN OccurredUtc ELSE NULL END) AS OldestPendingUtc
            FROM dbo.RequestPortalIntegrationOutbox;
            """;

        await using SqlCommand command = new(sql, connection);
        command.Parameters.Add("@MaxAttempts", SqlDbType.Int).Value =
            Math.Clamp(options.OutboxMaxAttempts, 1, 100);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        int pending = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0));
        int failed = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1));
        int locked = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2));
        DateTimeOffset? oldest = reader.IsDBNull(3)
            ? null
            : new DateTimeOffset(
                DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc));

        return new IntegrationOutboxSnapshot(pending, failed, locked, oldest);
    }

    public async Task<bool> SchemaExistsAsync(
        CancellationToken cancellationToken = default)
    {
        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT CASE
                WHEN COL_LENGTH(N'dbo.ApplicationLetters', N'ExternalRequestId') IS NOT NULL
                     AND OBJECT_ID(N'dbo.RequestPortalIntegrationOutbox', N'U') IS NOT NULL
                     AND OBJECT_ID(N'dbo.RequestPortal_EnqueueApplicationStatusChanged', N'P') IS NOT NULL
                     AND OBJECT_ID(N'dbo.RequestPortal_EnqueueInformationRequest', N'P') IS NOT NULL
                THEN CAST(1 AS bit)
                ELSE CAST(0 AS bit)
            END;
            """;
        await using SqlCommand command = new(sql, connection);
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return value is bool exists && exists;
    }

    private async Task<SqlConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.SqlConnectionString))
        {
            throw new InvalidOperationException(
                "FFP Manager SQL connection string is unavailable.");
        }

        return await FfpSqlConnection.OpenAsync(options.SqlConnectionString, options.SqlTarget, cancellationToken);
    }

    private int CalculateRetryDelaySeconds(int attemptCount)
    {
        int initial = Math.Clamp(options.OutboxRetryInitialSeconds, 1, 3600);
        int maximum = Math.Clamp(options.OutboxRetryMaxSeconds, initial, 86400);
        double multiplier = Math.Pow(2, Math.Clamp(attemptCount - 1, 0, 10));
        return Math.Min(maximum, checked((int)Math.Min(int.MaxValue, initial * multiplier)));
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength
            ? value
            : value[..maxLength];
}

public sealed class IntegrationOutboxDispatcher : BackgroundService
{
    private readonly IIntegrationOutboxStore store;
    private readonly IFfpManagerGateway gateway;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly IOptions<BridgeOptions> options;
    private readonly ILogger<IntegrationOutboxDispatcher> logger;

    public IntegrationOutboxDispatcher(
        IIntegrationOutboxStore store
        , IHttpClientFactory httpClientFactory
        , IOptions<BridgeOptions> options
        , ILogger<IntegrationOutboxDispatcher> logger
        , IFfpManagerGateway gateway)
    {
        this.store = store;
        this.gateway = gateway;
        this.httpClientFactory = httpClientFactory;
        this.options = options;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            BridgeOptions current = options.Value;
            if (current.EnableOutboxDispatch &&
                current.Gateway.Equals("Sql", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await DrainAsync(current, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception
                        , "FFP Manager integration outbox drain failed.");
                }
            }

            int seconds = Math.Clamp(current.OutboxDispatchIntervalSeconds, 1, 300);
            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
        }
    }

    private async Task DrainAsync(
        BridgeOptions current
        , CancellationToken cancellationToken)
    {
        int batchSize = Math.Clamp(current.OutboxBatchSize, 1, 100);
        for (int index = 0; index < batchSize; index++)
        {
            IntegrationOutboxMessage? message =
                await store.ClaimNextAsync(cancellationToken);
            if (message is null)
            {
                return;
            }

            try
            {
                await DispatchAsync(message, current, cancellationToken);
                await store.MarkDeliveredAsync(
                    message.SequenceNo
                    , cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await store.MarkFailedAsync(
                    message.SequenceNo
                    , message.AttemptCount
                    , $"{exception.GetType().Name}: {exception.Message}"
                    , cancellationToken);

                logger.LogWarning(
                    exception
                    , "Unable to deliver FFP integration event {EventId} ({EventType}) for ApplicationLetter {ApplicationId}."
                    , message.EventId
                    , message.EventType
                    , message.ApplicationId);
            }
        }
    }

    private Task DispatchAsync(
        IntegrationOutboxMessage message
        , BridgeOptions current
        , CancellationToken cancellationToken)
    {
        return message.EventType switch
        {
            "ApplicationStatusChanged" =>
                PostStatusAsync(message, current, cancellationToken),

            "InformationRequested" =>
                PostInformationRequestAsync(message, current, cancellationToken),

            _ => throw new InvalidOperationException(
                $"Unknown FFP integration event type '{message.EventType}'.")
        };
    }

    private async Task PostStatusAsync(
        IntegrationOutboxMessage message,
        BridgeOptions current,
        CancellationToken cancellationToken)
    {
        // Older outbox producers substituted Name/RECEIVED for missing Codes.
        // Read the authoritative Code through the same gateway as polling; do not
        // interpret those lossy snapshots or require any FFPJM procedure changes.
        ExternalApplicationStatus status = await gateway.GetStatusAsync(message.ApplicationId, cancellationToken)
            ?? throw new InvalidOperationException("The status callback application no longer exists.");

        ExternalStatusCallback callback = new(
            message.EventId
            , message.PortalRequestId
            , status);

        await PostAsync(
            "api/integration/ffpmanager/status"
            , callback
            , current
            , cancellationToken);
    }

    private Task PostInformationRequestAsync(
        IntegrationOutboxMessage message
        , BridgeOptions current
        , CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message.Message))
        {
            throw new InvalidOperationException(
                "InformationRequested outbox event contains no message.");
        }

        ExternalInformationRequest callback = new(
            message.EventId
            , message.PortalRequestId
            , message.ApplicationId
            , message.Message
            , message.OccurredUtc
            , message.RequestedBy
            , null
            , message.DueUtc);

        return PostAsync(
            "api/integration/ffpmanager/information-requests"
            , callback
            , current
            , cancellationToken);
    }

    private async Task PostAsync<T>(
        string path
        , T payload
        , BridgeOptions current
        , CancellationToken cancellationToken)
    {
        HttpClient client = httpClientFactory.CreateClient("RequestPortalCallback");
        if (client.BaseAddress is null)
        {
            throw new InvalidOperationException(
                "Bridge:RequestPortalBaseUrl is not configured.");
        }

        using HttpRequestMessage request = new(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload)
        };

        if (!string.IsNullOrWhiteSpace(current.SharedSecret))
        {
            request.Headers.TryAddWithoutValidation(
                "X-FFP-Integration-Key"
                , current.SharedSecret);
        }

        using HttpResponseMessage response =
            await client.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Request Portal callback returned {(int)response.StatusCode} {response.ReasonPhrase}: {detail}"
                , null
                , response.StatusCode);
        }
    }


}
