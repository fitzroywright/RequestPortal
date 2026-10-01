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
        app.UseHttpsRedirection();
        app.Use(AddSecurityHeadersAsync);
        app.UseStaticFiles();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.Use(ApplyAlternateAuthenticatedPrincipalAsync);
        app.UseAuthorization();
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
