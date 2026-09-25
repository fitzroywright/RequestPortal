using System.Net.Http.Headers;
using Common.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace FfpManager.Bridge;

public sealed class BridgeConfigurationDiagnosticLevelTest(
    IOptions<BridgeOptions> options
    , IHostEnvironment environment) : IDiagnosticLevelLocalTest
{
    public string TestId => "REQUESTPORTAL.FFPMANAGER.L5.CONFIG";
    public string Name => "FFP Manager bridge configuration";
    public string Owner => "FfpManager.Bridge";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level5Scan;
    public bool IsDestructive => false;

    public Task<EngineeringDiagnosticCheckResult> RunAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BridgeOptions current = options.Value;
        List<string> problems = [];

        if (!current.Gateway.Equals("Sql", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("Gateway is not Sql");
        }

        if (string.IsNullOrWhiteSpace(current.ServiceIdentity))
        {
            problems.Add("ServiceIdentity is missing");
        }

        if (!Uri.TryCreate(
                current.RequestPortalBaseUrl
                , UriKind.Absolute
                , out Uri? portalUri))
        {
            problems.Add("RequestPortalBaseUrl is invalid");
        }
        else if (environment.IsProduction() &&
                 portalUri.Scheme != Uri.UriSchemeHttps)
        {
            problems.Add("RequestPortalBaseUrl is not HTTPS");
        }

        if (string.IsNullOrWhiteSpace(current.SqlConnectionString))
        {
            problems.Add("SQL secret is unavailable");
        }

        if (string.IsNullOrWhiteSpace(current.SharedSecret))
        {
            problems.Add("callback shared secret is unavailable");
        }

        if (!current.EnableOutboxDispatch)
        {
            problems.Add("outbox dispatch is disabled");
        }

        return Task.FromResult(problems.Count == 0
            ? EngineeringDiagnosticPolicy.Passed(
                TestId
                , Name
                , "Bridge identity, SQL secret, callback secret, URL and outbox dispatch are configured.")
            : EngineeringDiagnosticPolicy.Failed(
                TestId
                , Name
                , "Bridge configuration is incomplete."
                , $"Problems={string.Join("; ", problems)}"));
    }
}

public sealed class BridgeSqlAndOutboxDiagnosticLevelTest(
    IOptions<BridgeOptions> options
    , IIntegrationOutboxStore outbox) : IDiagnosticLevelLocalTest
{
    public string TestId => "REQUESTPORTAL.FFPMANAGER.L4.SQL_OUTBOX";
    public string Name => "FFP Manager SQL and integration outbox";
    public string Owner => "FfpManager.Bridge";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level4Analysis;
    public bool IsDestructive => false;

    public async Task<EngineeringDiagnosticCheckResult> RunAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            string connectionString = options.Value.SqlConnectionString;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return EngineeringDiagnosticPolicy.Failed(
                    TestId
                    , Name
                    , "FFP Manager SQL secret is unavailable.");
            }

            await using SqlConnection connection = new(connectionString);
            await connection.OpenAsync(cancellationToken);

            bool schema = await outbox.SchemaExistsAsync(cancellationToken);
            if (!schema)
            {
                return EngineeringDiagnosticPolicy.Failed(
                    TestId
                    , Name
                    , "FFP Manager SQL is reachable but the Request Portal durable outbox schema is missing."
                    , "Install FfpManager.Bridge/Sql/001_RequestPortalIntegrationOutbox.sql.");
            }

            return EngineeringDiagnosticPolicy.Passed(
                TestId
                , Name
                , "FFP Manager SQL is reachable and the durable integration outbox schema is installed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return EngineeringDiagnosticPolicy.Failed(
                TestId
                , Name
                , "FFP Manager SQL/outbox validation failed."
                , $"FailureType={exception.GetType().Name}");
        }
    }
}

public sealed class BridgeReferenceDataDiagnosticLevelTest(
    IFfpManagerReferenceDataCatalog catalog
    , IOptions<BridgeOptions> options) : IDiagnosticLevelLocalTest
{
    public string TestId => "REQUESTPORTAL.FFPMANAGER.L3.REFERENCE_DATA";
    public string Name => "FFP Manager integration reference data";
    public string Owner => "FfpManager.Bridge";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level3Verification;
    public bool IsDestructive => false;

    public async Task<EngineeringDiagnosticCheckResult> RunAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            FfpManagerReferenceDataSnapshot snapshot =
                await catalog.GetSnapshotAsync(cancellationToken);

            List<string> problems = [];
            if (snapshot.ApplicationStatuses.Count == 0)
            {
                problems.Add("no application statuses");
            }

            if (snapshot.DocumentTypes.Count == 0)
            {
                problems.Add("no document types");
            }

            if (snapshot.UnitsOfMeasure.Count == 0)
            {
                problems.Add("no units of measure");
            }

            BridgeOptions current = options.Value;
            if (!string.IsNullOrWhiteSpace(current.InitialApplicationStatus))
            {
                await catalog.ResolveApplicationStatusIdAsync(
                    current.InitialApplicationStatus
                    , cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(current.DefaultDocumentType))
            {
                await catalog.ResolveDocumentTypeIdAsync(
                    current.DefaultDocumentType
                    , cancellationToken);
            }

            return problems.Count == 0
                ? EngineeringDiagnosticPolicy.Passed(
                    TestId
                    , Name
                    , "Reference data loaded and configured default mappings resolve."
                    , $"Statuses={snapshot.ApplicationStatuses.Count}; DocumentTypes={snapshot.DocumentTypes.Count}; Units={snapshot.UnitsOfMeasure.Count}")
                : EngineeringDiagnosticPolicy.Failed(
                    TestId
                    , Name
                    , "FFP Manager reference data is incomplete."
                    , $"Problems={string.Join("; ", problems)}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return EngineeringDiagnosticPolicy.Failed(
                TestId
                , Name
                , "Reference-data verification failed."
                , $"FailureType={exception.GetType().Name}; Message={exception.Message}");
        }
    }
}

public sealed class BridgeCallbackDiagnosticLevelTest(
    IHttpClientFactory httpClientFactory
    , IOptions<BridgeOptions> options) : IDiagnosticLevelLocalTest
{
    public string TestId => "REQUESTPORTAL.FFPMANAGER.L2.CALLBACK";
    public string Name => "Request Portal authenticated callback";
    public string Owner => "FfpManager.Bridge";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level2Repair;
    public bool IsDestructive => false;

    public async Task<EngineeringDiagnosticCheckResult> RunAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            BridgeOptions current = options.Value;
            HttpClient client = httpClientFactory.CreateClient("RequestPortalCallback");
            if (client.BaseAddress is null)
            {
                return EngineeringDiagnosticPolicy.Failed(
                    TestId
                    , Name
                    , "Request Portal callback BaseAddress is not configured.");
            }

            using HttpRequestMessage request = new(
                HttpMethod.Get
                , "api/integration/ffpmanager/probe");

            if (!string.IsNullOrWhiteSpace(current.SharedSecret))
            {
                request.Headers.TryAddWithoutValidation(
                    "X-FFP-Integration-Key"
                    , current.SharedSecret);
            }

            using HttpResponseMessage response =
                await client.SendAsync(request, cancellationToken);

            return response.IsSuccessStatusCode
                ? EngineeringDiagnosticPolicy.Passed(
                    TestId
                    , Name
                    , "Authenticated Request Portal callback probe succeeded.")
                : EngineeringDiagnosticPolicy.Failed(
                    TestId
                    , Name
                    , "Authenticated Request Portal callback probe failed."
                    , $"HttpStatus={(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return EngineeringDiagnosticPolicy.Failed(
                TestId
                , Name
                , "Request Portal callback probe failed."
                , $"FailureType={exception.GetType().Name}");
        }
    }
}

public sealed class BridgeRecoveryDiagnosticLevelTest(
    IIntegrationOutboxStore outbox
    , IOptions<BridgeOptions> options) : IDiagnosticLevelLocalTest
{
    public string TestId => "REQUESTPORTAL.FFPMANAGER.L1.RECOVERY";
    public string Name => "FFP Manager integration recovery state";
    public string Owner => "FfpManager.Bridge";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level1CriticalIntervention;
    public bool IsDestructive => false;

    public async Task<EngineeringDiagnosticCheckResult> RunAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            IntegrationOutboxSnapshot snapshot =
                await outbox.GetSnapshotAsync(cancellationToken);

            int maxAgeMinutes = Math.Clamp(
                options.Value.OutboxStaleAfterMinutes
                , 1
                , 10080);

            bool stale = snapshot.OldestPendingUtc.HasValue &&
                DateTimeOffset.UtcNow - snapshot.OldestPendingUtc.Value >
                TimeSpan.FromMinutes(maxAgeMinutes);

            string evidence =
                $"Pending={snapshot.Pending}; Failed={snapshot.Failed}; Locked={snapshot.Locked}; OldestPendingUtc={snapshot.OldestPendingUtc:O}";

            if (snapshot.Failed > 0)
            {
                return EngineeringDiagnosticPolicy.Failed(
                    TestId
                    , Name
                    , "One or more durable integration events exhausted their retry budget."
                    , evidence);
            }

            if (stale)
            {
                return EngineeringDiagnosticPolicy.Warning(
                    TestId
                    , Name
                    , "The durable integration outbox contains stale undelivered events."
                    , evidence);
            }

            return EngineeringDiagnosticPolicy.Passed(
                TestId
                , Name
                , "Durable outbox recovery state is healthy."
                , evidence);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return EngineeringDiagnosticPolicy.Failed(
                TestId
                , Name
                , "Deep recovery-state verification failed."
                , $"FailureType={exception.GetType().Name}");
        }
    }
}
