using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Administration;
using RequestPortal.Application.Portal;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Security;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class StaffPaginationResponseTests
{
    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string actor = Request.Headers["X-Test-Actor"].ToString();
            if (actor == "anonymous") return Task.FromResult(AuthenticateResult.NoResult());
            Claim[] claims = [new(ClaimTypes.NameIdentifier, actor),
                new(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue),
                new(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.Access)];
            return Task.FromResult(AuthenticateResult.Success(new(new(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.Redirect("/Account/StaffLogin?ReturnUrl=" + Uri.EscapeDataString(Request.Path + Request.QueryString));
            return Task.CompletedTask;
        }
    }

    private static async Task<WebApplication> Host()
    {
        var assembly = typeof(RequestPortal.Web.Pages.Staff.IndexModel).Assembly;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
            ApplicationName = assembly.GetName().Name, EnvironmentName = "Test",
            ContentRootPath = Path.GetFullPath("../../../../../RequestPortal.Web", AppContext.BaseDirectory) });
        builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddIdentityCore<ApplicationUser>();
        builder.Services.AddSingleton(MessageNavigationPageTests.Proxy.For<IUserStore<ApplicationUser>>((method, _) =>
            method == "Dispose" ? null : Task.FromResult<ApplicationUser?>(null)));
        builder.Services.AddSingleton(MessageNavigationPageTests.Proxy.For<IPortalWorkflowService>((_, _) =>
            Task.FromResult<IReadOnlyList<OrganizationSummary>>([])));
        builder.Services.AddSingleton(MessageNavigationPageTests.Proxy.For<IStaffOperationsService>((_, _) =>
            Task.FromResult(new StaffOperationsSnapshot([], [], [], [], [], [], []))));
        builder.Services.AddSingleton(MessageNavigationPageTests.Proxy.For<IStaffRequestSearchService>((method, args) => {
            int page = (int)args[method == "SearchAsync" ? 1 : 3]!;
            int size = (int)args[method == "SearchAsync" ? 2 : 4]!;
            return Task.FromResult(new StaffRequestSearchResult([new() { PublicReference = "PAGE-" + page }], 250, page, size, null));
        }));
        builder.Services.AddAuthentication(StaffOidcOptions.StaffCookieScheme)
            .AddScheme<AuthenticationSchemeOptions, Authentication>(StaffOidcOptions.StaffCookieScheme, _ => { })
            .AddScheme<AuthenticationSchemeOptions, Authentication>("ExternalReviewer", _ => { });
        assembly.GetType("RequestPortal.Web.Startup.PlatformServiceRegistration")!
            .GetMethod("AddRateLimiting", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [builder]);
        assembly.GetType("RequestPortal.Web.Startup.AuthorizationServiceRegistration")!.GetMethod("Add")!.Invoke(null, [builder]);
        builder.Services.AddPageAuthorizationFixture();
        var app = builder.Build();
        // Use the production middleware ordering, not a parallel test implementation.
        assembly.GetType("RequestPortal.Web.Startup.ApplicationPipeline")!.GetMethod("Configure")!.Invoke(null, [app]);
        app.MapRazorPages();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    [Fact]
    public async Task ActiveStaffHaveIndependentQuotasAndThrottleIsHtmlWithSamePageRetry()
    {
        await using var app = await Host();
        using var client = app.GetTestClient(); client.BaseAddress = new("https://localhost");
        string url = "/Staff?Applicant=A%26B&Organization=Org&WorkStatus=Submitted&PageNumber=2&PageSize=50";
        async Task<HttpResponseMessage> Get(string actor)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Test-Actor", actor);
            return await client.SendAsync(request, TestContext.Current.CancellationToken);
        }
        // Both staff share the same connection IP. Their combined traffic exceeds 120.
        for (int i = 0; i < 120; i++)
        {
            using var response = await Get(i % 2 == 0 ? "staff-a" : "staff-b");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Null(response.Content.Headers.ContentDisposition);
        }
        for (int i = 0; i < 60; i++) { using var response = await Get("staff-a"); Assert.Equal(HttpStatusCode.OK, response.StatusCode); }
        using var rejected = await Get("staff-a");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Null(rejected.Headers.Location); Assert.Null(rejected.Content.Headers.ContentDisposition);
        Assert.Equal("text/html", rejected.Content.Headers.ContentType?.MediaType);
        Assert.True(rejected.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        string html = await rejected.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("retry this page", html);
        Assert.Contains(HtmlEncoder.Default.Encode(url), html);
        using var other = await Get("staff-b"); Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        using var anonymous = await Get("anonymous");
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.StartsWith("/Account/StaffLogin?ReturnUrl=", anonymous.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("Applicant=A%26B&Organization=Org&WorkStatus=Submitted&PageSize=50")]
    [InlineData("Search=Jane%20Brown&PageSize=25")]
    public async Task GeneratedNextAndPreviousLinksPreserveQueryAndReturnHtmlRepeatedly(string query)
    {
        await using var app = await Host();
        using var client = app.GetTestClient(); client.BaseAddress = new("https://localhost");
        client.DefaultRequestHeaders.Add("X-Test-Actor", "staff-navigation");
        string url = "/Staff?" + query;
        for (int i = 0; i < 20; i++)
        {
            using var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Null(response.Headers.Location); Assert.Null(response.Content.Headers.ContentDisposition);
            string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            string direction = i % 2 == 0 ? "Next" : "Previous";
            var link = Regex.Match(html, "<a ([^>]+)>" + direction + "</a>");
            Assert.True(link.Success); Assert.DoesNotContain("download", link.Value);
            url = WebUtility.HtmlDecode(Regex.Match(link.Groups[1].Value, "href=\"([^\"]+)\"").Groups[1].Value);
            var values = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(client.BaseAddress, url).Query);
            foreach (var pair in Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery("?" + query)) Assert.Equal(pair.Value, values[pair.Key]);
            Assert.Equal(i % 2 == 0 ? "2" : "1", values["PageNumber"].ToString());
        }
    }
}
