using Microsoft.AspNetCore.Authentication;
using Common.Diagnostics;

namespace RequestPortal.Web.Startup;

internal static class ApplicationPipeline
{
    public static void Configure(WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
        }

        app.UseForwardedHeaders();
        app.Use(RecordRequestFailureAsync);
        app.UseHttpsRedirection();
        app.Use(AddSecurityHeadersAsync);
        app.UseStaticFiles();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.Use(ApplyAlternateAuthenticatedPrincipalAsync);
        app.Use(PreventAuthenticatedPageCachingAsync);
        app.UseAuthorization();
    }

    private static async Task RecordRequestFailureAsync(HttpContext context, Func<Task> next)
    {
        context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
        try
        {
            await next();
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ILogger logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RequestPortal.Http");
            logger.LogError(exception, "HTTP request failed. CorrelationId={CorrelationId}; Endpoint={Endpoint}",
                context.TraceIdentifier, context.GetEndpoint()?.DisplayName);
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
                await context.RequestServices.GetRequiredService<IDiagnosticOperationsRouter>().RouteAsync(
                    new DiagnosticTelemetryEvent(DateTimeOffset.UtcNow, "RequestPortal.Http",
                        "RP-HTTP-FAILED", DiagnosticSeverity.Error, "The HTTP request failed.",
                        context.TraceIdentifier, new Dictionary<string, string>
                        {
                            ["Endpoint"] = context.GetEndpoint()?.DisplayName ?? "Unknown",
                            ["Exception"] = exception.ToString()
                        }), timeout.Token);
            }
            catch (Exception diagnosticException)
            {
                logger.LogError(diagnosticException, "Diagnostic routing failed. CorrelationId={CorrelationId}", context.TraceIdentifier);
            }
            throw;
        }
    }

    private static async Task PreventAuthenticatedPageCachingAsync(HttpContext context, Func<Task> next)
    {
        if (context.User.Identity?.IsAuthenticated == true)
            context.Response.Headers.CacheControl = "no-store";
        await next();
    }

    private static async Task ApplyAlternateAuthenticatedPrincipalAsync(
        HttpContext context
        , Func<Task> next)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            Microsoft.AspNetCore.Authentication.AuthenticateResult staff =
                await context.AuthenticateAsync(
                    RequestPortal.Web.Security.StaffOidcOptions.StaffCookieScheme);

            if (staff.Succeeded && staff.Principal is not null)
            {
                context.User = staff.Principal;
            }
            else
            {
                Microsoft.AspNetCore.Authentication.AuthenticateResult reviewer =
                    await context.AuthenticateAsync("ExternalReviewer");

                if (reviewer.Succeeded && reviewer.Principal is not null)
                {
                    context.User = reviewer.Principal;
                }
            }
        }

        await next();
    }

    private static async Task AddSecurityHeadersAsync(
        HttpContext context
        , Func<Task> next)
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Referrer-Policy"] =
            "strict-origin-when-cross-origin";
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; img-src 'self' data: https:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline' https://cdnjs.cloudflare.com; connect-src 'self' ws: wss:; frame-ancestors 'none'; base-uri 'self'; form-action 'self' https://login.microsoftonline.com https://accounts.google.com https://www.facebook.com https://api.login.yahoo.com https://appleid.apple.com";
        context.Response.Headers["Permissions-Policy"] =
            "camera=(), microphone=(), geolocation=(self)";

        await next();
    }
}
