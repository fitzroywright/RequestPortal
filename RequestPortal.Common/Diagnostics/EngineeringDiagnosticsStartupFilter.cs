using System;
using System.Linq;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Common.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RequestPortal.Common.Diagnostics;

public sealed class EngineeringDiagnosticsStartupFilter : IStartupFilter
{
    private const string DefaultApiKeyHeader = "X-Aegis-Diagnostics-Key";
    private const string DefaultApiKeyEnvironmentVariable = "REQUESTPORTAL_DIAGNOSTICS_KEY";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(async (context, continuePipeline) =>
            {
                await continuePipeline();

                if (context.Response.HasStarted || context.Response.StatusCode != StatusCodes.Status404NotFound)
                {
                    return;
                }

                if (context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Clear();
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        status = "running",
                        application = "RequestPortal",
                        timeUtc = DateTimeOffset.UtcNow
                    });
                    return;
                }

                if (!context.Request.Path.StartsWithSegments("/api/engineering/diagnostics"))
                {
                    return;
                }

                context.Response.Clear();
                if (!IsAuthorized(context))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                await HandleDiagnosticsRequestAsync(context);
            });

            next(app);
        };
    }

    private static async Task HandleDiagnosticsRequestAsync(HttpContext context)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        IServiceProvider services = context.RequestServices;
        IEngineeringDiagnosticRunStore store = services.GetRequiredService<IEngineeringDiagnosticRunStore>();

        if (HttpMethods.IsGet(context.Request.Method) && path.Equals("/api/engineering/diagnostics/runs", StringComparison.OrdinalIgnoreCase))
        {
            int take = 25;
            if (int.TryParse(context.Request.Query["take"], out int requested)) take = Math.Clamp(requested, 1, 100);
            await context.Response.WriteAsJsonAsync(await store.GetRecentAsync(take, context.RequestAborted));
            return;
        }

        string prefix = "/api/engineering/diagnostics/runs/";
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            string remainder = path[prefix.Length..];
            bool resolve = remainder.EndsWith("/resolve", StringComparison.OrdinalIgnoreCase);
            string idText = resolve ? remainder[..^"/resolve".Length] : remainder;
            if (!Guid.TryParse(idText.Trim('/'), out Guid runId))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (HttpMethods.IsGet(context.Request.Method) && !resolve)
            {
                EngineeringDiagnosticRun? run = await store.GetAsync(runId, context.RequestAborted);
                if (run is null) context.Response.StatusCode = StatusCodes.Status404NotFound;
                else await context.Response.WriteAsJsonAsync(run);
                return;
            }

            if (HttpMethods.IsPost(context.Request.Method) && resolve)
            {
                EngineeringDiagnosticResolutionRequest? request = await context.Request.ReadFromJsonAsync<EngineeringDiagnosticResolutionRequest>(context.RequestAborted);
                if (request is null || string.IsNullOrWhiteSpace(request.Resolution))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsJsonAsync(new { error = "Resolution is required." });
                    return;
                }

                EngineeringDiagnosticRun? resolved = await store.ResolveAsync(runId, Actor(context), request.Resolution.Trim(), context.RequestAborted);
                if (resolved is null) context.Response.StatusCode = StatusCodes.Status404NotFound;
                else await context.Response.WriteAsJsonAsync(resolved);
                return;
            }
        }

        if (HttpMethods.IsPost(context.Request.Method) && path.Equals("/api/engineering/diagnostics/run", StringComparison.OrdinalIgnoreCase))
        {
            EngineeringDiagnosticRunRequest? request = await context.Request.ReadFromJsonAsync<EngineeringDiagnosticRunRequest>(context.RequestAborted);
            if (request is null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            EngineeringDiagnosticEngine engine = services.GetRequiredService<EngineeringDiagnosticEngine>();
            RequestPortalEngineeringDiagnosticCatalog catalog = services.GetRequiredService<RequestPortalEngineeringDiagnosticCatalog>();
            IHostEnvironment environment = services.GetRequiredService<IHostEnvironment>();
            EngineeringDiagnosticRun run = await engine.RunAsync(
                request.Level,
                "RequestPortal",
                environment.EnvironmentName,
                Actor(context),
                request.Reason,
                catalog.Build(),
                context.RequestAborted);
            await context.Response.WriteAsJsonAsync(run);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private static bool IsAuthorized(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            return true;
        }

        IConfiguration configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        string header = configuration["Diagnostics:ApiKeyHeader"] ?? DefaultApiKeyHeader;
        string environmentVariable = configuration["Diagnostics:ApiKeyEnvironmentVariable"] ?? DefaultApiKeyEnvironmentVariable;
        string? expected = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(expected) || !context.Request.Headers.TryGetValue(header, out var supplied))
        {
            return false;
        }

        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        byte[] suppliedBytes = Encoding.UTF8.GetBytes(supplied.ToString());
        return expectedBytes.Length == suppliedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private static string Actor(HttpContext context)
        => context.User.Identity?.Name ?? "Aegis.Diagnostics";
}
