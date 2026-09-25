using Common.Diagnostics;

namespace RequestPortal.Web.Startup;

internal static class ApplicationPipeline
{
    public static void Configure(WebApplication app)
    {
        app.MapDiagnosticLevelRuntime();

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
        }

        app.UseForwardedHeaders();
        app.UseHttpsRedirection();
        app.Use(AddSecurityHeadersAsync);
        app.UseStaticFiles();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
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
            "default-src 'self'; img-src 'self' data: https:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline' https://cdnjs.cloudflare.com; connect-src 'self' ws: wss:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
        context.Response.Headers["Permissions-Policy"] =
            "camera=(), microphone=(), geolocation=(self)";

        await next();
    }
}
