using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using RequestPortal.Application.Integration;
using FfpManager.Bridge;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<JsonOptions>(options => options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection(BridgeOptions.SectionName));
builder.Services.AddSingleton<IFfpManagerGateway, FakeFfpManagerGateway>();
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
if (builder.Environment.IsProduction() && string.IsNullOrWhiteSpace(sharedSecret))
{
    throw new InvalidOperationException("Bridge:SharedSecret is required in production.");
}

WebApplication app = builder.Build();
app.UseHttpsRedirection();
app.MapGet("/health", () => Results.Ok(new { status = "ok", gateway = "fake" }));

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
    public string SharedSecret { get; set; } = string.Empty;
    public string RequestPortalBaseUrl { get; set; } = string.Empty;
    public int CallbackTimeoutSeconds { get; set; } = 30;
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
    DateTimeOffset? DueUtc = null);
