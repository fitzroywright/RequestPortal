using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Diagnostics;
using Common.Secrets;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;
using FfpManager.Bridge;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

await using CommonSecretsBootstrapRuntime secretsRuntime =
    CommonSecretsBootstrapRuntime.Create(builder.Configuration);
secretsRuntime.Register(builder.Services);

string sharedSecretName =
    builder.Configuration["Bridge:SharedSecretSecretName"]?.Trim()
    ?? "FfpManagerIntegration:SharedSecret";
string sqlConnectionSecretName =
    builder.Configuration["Bridge:SqlConnectionStringSecretName"]?.Trim()
    ?? "FfpManagerBridge:SqlConnectionString";

string? resolvedSharedSecret =
    await secretsRuntime.Provider.GetAsync(sharedSecretName);
string? resolvedSqlConnection =
    await secretsRuntime.Provider.GetAsync(sqlConnectionSecretName);

if (!builder.Environment.IsProduction())
{
    resolvedSharedSecret ??= builder.Configuration["Bridge:SharedSecret"];
    resolvedSqlConnection ??= builder.Configuration["Bridge:SqlConnectionString"];
}

builder.Configuration["Bridge:SharedSecret"] = resolvedSharedSecret ?? string.Empty;
builder.Configuration["Bridge:SqlConnectionString"] = resolvedSqlConnection ?? string.Empty;

builder.Services.Configure<JsonOptions>(
    options => options.SerializerOptions.PropertyNamingPolicy =
        JsonNamingPolicy.CamelCase);
builder.Services.Configure<BridgeOptions>(
    builder.Configuration.GetSection(BridgeOptions.SectionName));

string serviceIdentity =
    builder.Configuration["Bridge:ServiceIdentity"]?.Trim()
    ?? string.Empty;

builder.Services.AddCommonDiagnostics();
builder.Services.AddCommonSecretsDiagnostics();
builder.Services.AddSingleton(new DiagnosticLevelExecutionOptions
{
    Application = "RequestPortal",
    Component = "FfpManager.Bridge",
    InstanceId = string.IsNullOrWhiteSpace(serviceIdentity)
        ? Environment.MachineName
        : serviceIdentity,
    CatalogVersion = "1"
});
builder.Services.AddSingleton(new DiagnosticLevelStoreOptions
{
    Path = builder.Configuration["Aegis:Diagnostics:StorePath"]?.Trim()
        ?? Path.Combine(
            builder.Environment.ContentRootPath,
            "data",
            "diagnostic-level",
            "ffpmanager-bridge.db")
});
builder.Services.AddSingleton<IDiagnosticLevelRequestCredentialProvider, CommonSecretsDiagnosticLevelRequestCredentialProvider>();
builder.Services.AddSingleton<IDiagnosticLevelLocalTest, BridgeConfigurationDiagnosticLevelTest>();
builder.Services.AddSingleton<IDiagnosticLevelLocalTest, BridgeSqlAndOutboxDiagnosticLevelTest>();
builder.Services.AddSingleton<IDiagnosticLevelLocalTest, BridgeReferenceDataDiagnosticLevelTest>();
builder.Services.AddSingleton<IDiagnosticLevelLocalTest, BridgeCallbackDiagnosticLevelTest>();
builder.Services.AddSingleton<IDiagnosticLevelLocalTest, BridgeRecoveryDiagnosticLevelTest>();

string gatewayMode = (builder.Configuration["Bridge:Gateway"] ?? "Fake").Trim();
if (!gatewayMode.Equals("Fake", StringComparison.OrdinalIgnoreCase) &&
    !gatewayMode.Equals("Sql", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Bridge:Gateway must be either 'Fake' or 'Sql'.");
}

builder.Services.AddSingleton<FakeFfpManagerGateway>();
builder.Services.AddSingleton<SqlFfpManagerGateway>();
builder.Services.AddSingleton<IFfpManagerReferenceDataCatalog, FfpManagerReferenceDataCatalog>();
builder.Services.AddSingleton<IIntegrationOutboxStore, SqlIntegrationOutboxStore>();
builder.Services.AddSingleton<SyncedSqlFfpManagerGateway>();
builder.Services.AddHostedService<IntegrationOutboxDispatcher>();
builder.Services.AddSingleton<IFfpManagerGateway>(services =>
    gatewayMode.Equals("Sql", StringComparison.OrdinalIgnoreCase)
        ? services.GetRequiredService<SyncedSqlFfpManagerGateway>()
        : services.GetRequiredService<FakeFfpManagerGateway>());

builder.Services.AddHttpClient("RequestPortalCallback", client =>
{
    string? baseUrl = builder.Configuration["Bridge:RequestPortalBaseUrl"];
    if (!string.IsNullOrWhiteSpace(baseUrl))
    {
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
    }
    client.Timeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("Bridge:CallbackTimeoutSeconds", 30));
});

string sharedSecret = builder.Configuration["Bridge:SharedSecret"] ?? string.Empty;
long maxArtifactBytes = Math.Max(
    1,
    builder.Configuration.GetValue<long?>("Bridge:MaxArtifactBytes") ?? 25L * 1024L * 1024L);

if (gatewayMode.Equals("Sql", StringComparison.OrdinalIgnoreCase))
{
    if (string.IsNullOrWhiteSpace(builder.Configuration["Bridge:SqlConnectionString"]))
    {
        throw new InvalidOperationException("Bridge:SqlConnectionString is required when Bridge:Gateway is Sql.");
    }

    if (string.IsNullOrWhiteSpace(builder.Configuration["Bridge:SupportingDocumentRoot"]))
    {
        throw new InvalidOperationException("Bridge:SupportingDocumentRoot is required when Bridge:Gateway is Sql so RequestPortal artifacts can be stored for FFP Manager.");
    }

    if (string.IsNullOrWhiteSpace(builder.Configuration["Bridge:InitialApplicationStatus"]))
    {
        throw new InvalidOperationException("Bridge:InitialApplicationStatus is required when Bridge:Gateway is Sql.");
    }
}

if (builder.Environment.IsProduction())
{
    if (secretsRuntime.CommonOptions.Mode != SecretsEnvironmentMode.Production)
    {
        throw new InvalidOperationException("Common.Secrets must run in Production mode for the production bridge.");
    }

    if (string.IsNullOrWhiteSpace(serviceIdentity))
    {
        throw new InvalidOperationException("Bridge:ServiceIdentity is required in production.");
    }

    if (string.IsNullOrWhiteSpace(sharedSecret))
    {
        throw new InvalidOperationException("Bridge:SharedSecret is required in production.");
    }

    if (gatewayMode.Equals("Fake", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("The fake FFP Manager gateway cannot be used in production.");
    }

    string? portalBaseUrl = builder.Configuration["Bridge:RequestPortalBaseUrl"];
    if (!Uri.TryCreate(portalBaseUrl, UriKind.Absolute, out Uri? portalUri) || portalUri.Scheme != Uri.UriSchemeHttps)
    {
        throw new InvalidOperationException("Bridge:RequestPortalBaseUrl must be an absolute HTTPS URL in production.");
    }

    BridgeOptions productionOptions = builder.Configuration
        .GetSection(BridgeOptions.SectionName)
        .Get<BridgeOptions>() ?? new BridgeOptions();
    if (!productionOptions.EnableOutboxDispatch)
    {
        throw new InvalidOperationException("Bridge:EnableOutboxDispatch must be enabled in production.");
    }
    if (productionOptions.StatusMap.Count == 0)
    {
        throw new InvalidOperationException("Bridge:StatusMap must define the FFP-to-Portal status contract in production.");
    }
    if (productionOptions.DocumentTypeMap.Count == 0)
    {
        throw new InvalidOperationException("Bridge:DocumentTypeMap must define the Portal-to-FFP document contract in production.");
    }

    string allowedHosts = builder.Configuration["AllowedHosts"] ?? string.Empty;
    if (string.IsNullOrWhiteSpace(allowedHosts) || allowedHosts == "*")
    {
        throw new InvalidOperationException("AllowedHosts must be restricted in production.");
    }
}

WebApplication app = builder.Build();
app.MapDiagnosticLevelRuntime();
app.UseHttpsRedirection();

if (gatewayMode.Equals("Sql", StringComparison.OrdinalIgnoreCase))
{
    await app.Services.GetRequiredService<IFfpManagerReferenceDataCatalog>()
        .RefreshAsync(app.Lifetime.ApplicationStopping);
}

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    gateway = gatewayMode,
    serviceIdentity,
    outbox = builder.Configuration.GetValue("Bridge:EnableOutboxDispatch", true),
    maxArtifactBytes
}));

RouteGroupBuilder api = app.MapGroup("/api");
api.AddEndpointFilter(async (context, next) =>
{
    HttpContext http = context.HttpContext;
    if (!IsAuthorized(http.Request, sharedSecret, app.Environment.IsDevelopment()))
    {
        return Results.Unauthorized();
    }
    return await next(context);
});

api.MapGet("/reference-data", async (
    IFfpManagerReferenceDataCatalog catalog,
    CancellationToken cancellationToken) =>
{
    if (!gatewayMode.Equals("Sql", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "Reference-data synchronization is available only when Bridge:Gateway is Sql." });
    }

    return Results.Ok(await catalog.GetSnapshotAsync(cancellationToken));
});

api.MapPost("/reference-data/refresh", async (
    IFfpManagerReferenceDataCatalog catalog,
    CancellationToken cancellationToken) =>
{
    if (!gatewayMode.Equals("Sql", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "Reference-data synchronization is available only when Bridge:Gateway is Sql." });
    }

    await catalog.RefreshAsync(cancellationToken);
    return Results.Ok(await catalog.GetSnapshotAsync(cancellationToken));
});

api.MapPost("/applications", async (
    FfpManagerApplicationSubmission submission,
    IFfpManagerGateway gateway,
    CancellationToken cancellationToken) =>
{
    FfpManagerApplicationReference reference = await gateway.CreateApplicationAsync(submission, cancellationToken);
    return Results.Ok(reference);
});

api.MapPut("/applications/{applicationId}/artifacts/{documentId:guid}", async (
    string applicationId,
    Guid documentId,
    HttpRequest request,
    IFfpManagerGateway gateway,
    CancellationToken cancellationToken) =>
{
    if (request.ContentLength is long contentLength && contentLength > maxArtifactBytes + 1024L * 1024L)
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    IFormCollection form = await request.ReadFormAsync(cancellationToken);
    string metadataJson = form["metadata"].ToString();
    FfpManagerArtifactDescriptor? descriptor = JsonSerializer.Deserialize<FfpManagerArtifactDescriptor>(
        metadataJson,
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    IFormFile? file = form.Files.GetFile("file");
    if (descriptor is null || file is null || descriptor.PortalDocumentId != documentId)
    {
        return Results.BadRequest("Valid artifact metadata and file are required.");
    }
    if (file.Length > maxArtifactBytes || descriptor.SizeBytes > maxArtifactBytes || descriptor.SizeBytes != file.Length)
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    await using Stream stream = file.OpenReadStream();
    await gateway.SaveArtifactAsync(applicationId, descriptor, stream, cancellationToken);
    return Results.NoContent();
});

api.MapGet("/applications/{applicationId}", async (
    string applicationId,
    IFfpManagerGateway gateway,
    CancellationToken cancellationToken) =>
{
    FfpManagerApplicationSubmission? application = await gateway.GetApplicationAsync(applicationId, cancellationToken);
    return application is null ? Results.NotFound() : Results.Ok(application);
});

api.MapGet("/applications/{applicationId}/status", async (
    string applicationId,
    IFfpManagerGateway gateway,
    CancellationToken cancellationToken) =>
{
    FfpManagerApplicationStatus? status = await gateway.GetStatusAsync(applicationId, cancellationToken);
    return status is null ? Results.NotFound() : Results.Ok(status);
});

api.MapPost("/applications/{applicationId}/information-responses", async (
    string applicationId,
    FfpManagerInformationResponse response,
    IFfpManagerGateway gateway,
    CancellationToken cancellationToken) =>
{
    if (!string.Equals(applicationId, response.ApplicationId, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest("Application id mismatch.");
    }

    await gateway.SaveInformationResponseAsync(response, cancellationToken);
    return Results.Accepted();
});

if (app.Environment.IsDevelopment())
{
    api.MapPost("/dev/applications/{applicationId}/status", async (
        string applicationId,
        SetStatusRequest request,
        IFfpManagerGateway gateway,
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken) =>
    {
        FfpManagerApplicationSubmission? application = await gateway.GetApplicationAsync(applicationId, cancellationToken);
        if (application is null)
        {
            return Results.NotFound();
        }

        FfpManagerApplicationStatus status = await gateway.SetStatusAsync(
            applicationId,
            request.StatusCode,
            request.StatusText,
            request.Detail,
            request.IsTerminal,
            cancellationToken);
        FfpManagerStatusCallback callback = new(Guid.NewGuid(), application.PortalRequestId, status);
        await PostCallbackAsync(
            httpClientFactory,
            "api/integration/ffpmanager/status",
            callback,
            sharedSecret,
            cancellationToken);
        return Results.Ok(callback);
    });

    api.MapPost("/dev/applications/{applicationId}/information-requests", async (
        string applicationId,
        CreateInformationRequest request,
        IFfpManagerGateway gateway,
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken) =>
    {
        FfpManagerApplicationSubmission? application = await gateway.GetApplicationAsync(applicationId, cancellationToken);
        if (application is null)
        {
            return Results.NotFound();
        }

        FfpManagerInformationRequest callback = new(
            Guid.NewGuid(),
            application.PortalRequestId,
            applicationId,
            request.Message,
            DateTimeOffset.UtcNow,
            request.RequestedBy,
            request.RequestedArtifacts,
            request.DueUtc);
        await PostCallbackAsync(
            httpClientFactory,
            "api/integration/ffpmanager/information-requests",
            callback,
            sharedSecret,
            cancellationToken);
        return Results.Ok(callback);
    });
}

await app.RunAsync();

static bool IsAuthorized(HttpRequest request, string expectedSecret, bool development)
{
    if (string.IsNullOrEmpty(expectedSecret))
    {
        return development;
    }

    string supplied = request.Headers["X-FFP-Integration-Key"].ToString();
    byte[] expected = Encoding.UTF8.GetBytes(expectedSecret);
    byte[] actual = Encoding.UTF8.GetBytes(supplied);
    return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
}

static async Task PostCallbackAsync<T>(
    IHttpClientFactory httpClientFactory,
    string relativeUrl,
    T payload,
    string sharedSecret,
    CancellationToken cancellationToken)
{
    HttpClient client = httpClientFactory.CreateClient("RequestPortalCallback");
    if (client.BaseAddress is null)
    {
        throw new InvalidOperationException("Bridge:RequestPortalBaseUrl is not configured.");
    }

    using HttpRequestMessage request = new(HttpMethod.Post, relativeUrl)
    {
        Content = JsonContent.Create(payload)
    };
    if (!string.IsNullOrWhiteSpace(sharedSecret))
    {
        request.Headers.TryAddWithoutValidation("X-FFP-Integration-Key", sharedSecret);
    }

    using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
    response.EnsureSuccessStatusCode();
}

public sealed class BridgeOptions
{
    public const string SectionName = "Bridge";
    public string ServiceIdentity { get; set; } = string.Empty;
    public string SharedSecretSecretName { get; set; } = "FfpManagerIntegration:SharedSecret";
    public string SqlConnectionStringSecretName { get; set; } = "FfpManagerBridge:SqlConnectionString";
    public string SharedSecret { get; set; } = string.Empty;
    public string RequestPortalBaseUrl { get; set; } = string.Empty;
    public int CallbackTimeoutSeconds { get; set; } = 30;
    public long MaxArtifactBytes { get; set; } = 25L * 1024L * 1024L;
    public string Gateway { get; set; } = "Fake";
    public string SqlConnectionString { get; set; } = string.Empty;
    public string SupportingDocumentRoot { get; set; } = string.Empty;
    public string UserName { get; set; } = "RequestPortal";
    public string CreatedBy { get; set; } = "RequestPortal";
    public string InitialApplicationStatus { get; set; } = string.Empty;
    public string DefaultDocumentType { get; set; } = string.Empty;
    public string InformationResponseNoteType { get; set; } = "RequestPortal.InformationResponse";
    public int ReferenceDataRefreshSeconds { get; set; } = 300;
    public bool EnableOutboxDispatch { get; set; } = true;
    public int OutboxDispatchIntervalSeconds { get; set; } = 5;
    public int OutboxBatchSize { get; set; } = 20;
    public int OutboxLockSeconds { get; set; } = 60;
    public int OutboxMaxAttempts { get; set; } = 10;
    public int OutboxRetryInitialSeconds { get; set; } = 5;
    public int OutboxRetryMaxSeconds { get; set; } = 900;
    public int OutboxStaleAfterMinutes { get; set; } = 15;
    public Dictionary<string, string> DocumentTypeMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> StatusMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record SetStatusRequest(
    string StatusCode,
    string StatusText,
    string? Detail = null,
    bool IsTerminal = false);

public sealed record CreateInformationRequest(
    string Message,
    string? RequestedBy = null,
    IReadOnlyList<string>? RequestedArtifacts = null,
    DateTimeOffset? DueUtc = null,
    Guid? MessageId = null,
    DateTimeOffset? RequestedUtc = null);
