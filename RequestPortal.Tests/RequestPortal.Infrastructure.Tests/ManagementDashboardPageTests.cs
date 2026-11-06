using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Management;
using RequestPortal.Common.Security;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Security;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class ManagementDashboardPageTests
{
    public class Users : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name == "Dispose" ? null :
            Task.FromResult<ApplicationUser?>(new() { Id = Guid.Parse("11111111-1111-4111-8111-111111111111"), DisplayName = "Fixture manager" });
    }
    private sealed class Dashboard(bool deliveryData, bool longNames) : IManagementDashboardService
    {
        public int Calls;
        public ManagementRequestFilter? LastFilter;
        public ManagementDashboardSnapshot Snapshot { get; } = Build(deliveryData, longNames);
        public Task<ManagementDashboardSnapshot> GetDashboardAsync(CancellationToken token = default) { Calls++; return Task.FromResult(Snapshot); }
        public Task<ManagementDashboardSnapshot> GetDashboardAsync(ManagementDashboardFilter filter, CancellationToken token = default) => GetDashboardAsync(token);
        public Task<IReadOnlyList<ManagementRequestRow>> GetRequestsAsync(ManagementRequestFilter filter, CancellationToken token = default) { Calls++; LastFilter = filter; return Task.FromResult<IReadOnlyList<ManagementRequestRow>>([]); }
        public Task<ParishDrillDown> GetParishAsync(string parish, CancellationToken token = default) { Calls++; return Task.FromResult(new ParishDrillDown(parish, [], [])); }
        public Task<IReadOnlyList<ManagementDeliveryRow>> GetDeliveriesAsync(ManagementDeliveryFilter filter, CancellationToken token = default) { Calls++; return Task.FromResult<IReadOnlyList<ManagementDeliveryRow>>([]); }
    }
    private static ManagementDashboardSnapshot Build(bool deliveryData, bool longNames)
    {
        var date = new DateOnly(2026, 10, 5);
        var parishes = JamaicaParishes.Names.Append(JamaicaParishes.Unknown).Select(name => new ParishReachSummary(name,
            name == "St. Catherine" ? 5 : name == "Kingston" ? 2 : name == JamaicaParishes.Unknown ? 4 : 0,
            0, 0, 0, name == "St. Catherine" ? 4 : name == "Kingston" ? 1 : name == JamaicaParishes.Unknown ? 2 : 0,
            name is "St. Catherine" or "Kingston" ? 1 : 0)).ToArray();
        string name = longNames ? new string('W', 180) + " Long recorded organization/program/category" : "Recorded classification";
        var dimensions = deliveryData ? new[] { new ManagementDeliveryDimension(name, 3, 2, 1, 1) } : Array.Empty<ManagementDeliveryDimension>();
        return new(new(2026, 10, 26, 15, 0, 0, TimeSpan.Zero), new(3, 8, 11, 7, 2),
            [new("new", "New", 3), new("under-review", "Under review", 2), new("awaiting-info", "Awaiting info", 1), new("approved", "Approved", 1), new("declined", "Declined", 2), new("completed", "Completed", 2)],
            new(4, 5, 6), [new(name, 6), new("Second recorded category", 5)], [new(name, 6), new("Second recorded program", 5)],
            deliveryData ? new(3, 2, 1, 1, 2) : new(0, 0, 0, 0, 0),
            new(dimensions, dimensions, dimensions, Enumerable.Range(0, 4).Select(i => new ManagementDeliveryTrendPoint(date.AddDays(i * 7), deliveryData && i == 1 ? 2 : 0, deliveryData && i == 2 ? 1 : 0, deliveryData && i == 3 ? 1 : 0)).ToArray()),
            new(4.5, 3.5, 8.2, 18.2), Enumerable.Range(0, 4).Select(i => new ManagementTrendPoint(date.AddDays(i * 7), i + 1, i == 3 ? 2 : 0)).ToArray(), parishes);
    }
    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string actor = Request.Headers["X-Test-Actor"].ToString();
            if (actor == "anonymous") return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "11111111-1111-4111-8111-111111111111"), new(ClaimTypes.Name, "Fixture manager") };
            if (actor != "public") claims.Add(new(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue));
            claims.Add(new(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.Access));
            if (actor != "no-management") claims.Add(new(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.ViewManagementDashboard));
            if (actor == "Administrator") claims.Add(new(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.ManageSecurity));
            return Task.FromResult(AuthenticateResult.Success(new(new(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public required IHost Host;
        public required HttpClient Client;
        public required Dashboard Service;
        public async ValueTask DisposeAsync() { Client.Dispose(); await Host.StopAsync(); Host.Dispose(); }
    }
    private static async Task<Fixture> Setup(string actor = "Administrator", bool deliveries = true, bool longNames = false)
    {
        var service = new Dashboard(deliveries, longNames);
        string root = Path.GetFullPath("../../../../../RequestPortal.Web", AppContext.BaseDirectory);
        var host = new HostBuilder().ConfigureWebHost(web => web.UseTestServer().UseEnvironment("Test").UseContentRoot(root).UseWebRoot("wwwroot")
            .UseSetting(WebHostDefaults.ApplicationKey, typeof(RequestPortal.Web.Pages.Management.IndexModel).Assembly.GetName().Name)
            .ConfigureServices(services =>
            {
                services.AddDataProtection().UseEphemeralDataProtectionProvider(); services.AddIdentityCore<ApplicationUser>();
                services.AddSingleton(DispatchProxy.Create<IUserStore<ApplicationUser>, Users>());
                services.AddSingleton<IManagementDashboardService>(service);
                services.AddAuthentication(StaffOidcOptions.StaffCookieScheme).AddScheme<AuthenticationSchemeOptions, Authentication>(StaffOidcOptions.StaffCookieScheme, _ => { });
                services.AddPageAuthorizationFixture();
                services.AddAuthorization(options => options.AddPolicy(PortalPolicies.ManagementDashboard, policy =>
                {
                    policy.AuthenticationSchemes.Add(StaffOidcOptions.StaffCookieScheme); policy.RequireAuthenticatedUser();
                    policy.RequireClaim(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue);
                    policy.RequireClaim(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.ViewManagementDashboard);
                }));
                services.AddRazorPages(options => options.Conventions.AuthorizeFolder("/Management", PortalPolicies.ManagementDashboard))
                    .AddApplicationPart(typeof(RequestPortal.Web.Pages.Management.IndexModel).Assembly);
            }).Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapRazorPages()); })).Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var client = host.GetTestServer().CreateClient(); client.BaseAddress = new("https://localhost"); client.DefaultRequestHeaders.Add("X-Test-Actor", actor);
        return new() { Host = host, Client = client, Service = service };
    }
    [Theory]
    [InlineData("Administrator", true, false)]
    [InlineData("Reviewer", false, false)]
    [InlineData("Administrator", true, true)]
    public async Task NativePagePreservesAllSectionsActualCountsLinksAndAggregateOnlyPayload(string actor, bool deliveries, bool longNames)
    {
        await using var fixture = await Setup(actor, deliveries, longNames);
        using var response = await fixture.Client.GetAsync("/Management", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.Service.Calls);
        Assert.Matches("<a class=\"brand[^\"]*\" href=\"/Staff\"", html);
        foreach (var title in new[] { "Service Reach Across Jamaica", "Operational Insights", "Request Status", "Delivery / Proof of Delivery", "Delivery by Organization", "Delivery by Program", "Delivery by Parish", "Delivery Outcome Trend", "Requests by Category", "Requests by Program", "Received vs. Completed" }) Assert.Contains(title, html);
        Assert.Contains("/Management/Requests?period=today", html); Assert.Contains("/Management/Requests?attention=missing-docs", html);
        Assert.Contains("/Management/Requests?status=under-review", html); Assert.Contains("/Management/Deliveries?status=Ready", html);
        Assert.DoesNotContain("jamaica-map", html); Assert.DoesNotContain("<circle", html);
        Assert.Contains("name=\"parish\"", html); Assert.Contains("St. Catherine (5)", html);
        Assert.Contains("Unknown / not provided: 4", html);
        Assert.Contains("width:100%", html); Assert.Contains("width:33.33%", html);
        var match = Regex.Match(html, "id=\"management-dashboard-data\">(.*?)</script>", RegexOptions.Singleline);
        Assert.True(match.Success); using var json = JsonDocument.Parse(match.Groups[1].Value);
        Assert.Equal(new[] { "delivery", "parishes", "requests" }, json.RootElement.EnumerateObject().Select(property => property.Name).Order());
        var parishes = json.RootElement.GetProperty("parishes").EnumerateArray().ToArray();
        Assert.Equal(15, parishes.Length); Assert.Equal(11, parishes.Sum(item => item.GetProperty("total").GetInt32()));
        foreach (var parish in parishes) Assert.Equal(new[] { "completed", "name", "outstanding", "total" }, parish.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(deliveries ? 2 : 0, json.RootElement.GetProperty("delivery").EnumerateArray().Sum(item => item.GetProperty("delivered").GetInt32()));
        if (!deliveries) { Assert.Equal(3, Regex.Matches(html, "No delivery data yet").Count); Assert.DoesNotContain("<th>Waiting</th>", html); }
        string? output = Environment.GetEnvironmentVariable("REQUESTPORTAL_DASHBOARD_RENDER_OUTPUT");
        if (!string.IsNullOrEmpty(output)) { Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, longNames ? "long.html" : deliveries ? "full.html" : "zero.html"), html); }
    }
    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("public", HttpStatusCode.Forbidden)]
    [InlineData("no-management", HttpStatusCode.Forbidden)]
    public async Task BackendProtectsDashboardAndParishDrilldowns(string actor, HttpStatusCode expected)
    {
        await using var fixture = await Setup(actor);
        foreach (string url in new[] { "/Management", "/Management/Requests?parish=St.%20Catherine", "/Management/Parish?parish=Kingston", "/Management/Deliveries" })
        {
            using var response = await fixture.Client.GetAsync(url, TestContext.Current.CancellationToken);
            Assert.Equal(expected, response.StatusCode);
            Assert.DoesNotContain("management-dashboard-data", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        Assert.Equal(0, fixture.Service.Calls);
    }
    [Theory]
    [InlineData("St. Catherine")]
    [InlineData(JamaicaParishes.Unknown)]
    public async Task ParishFilterUsesExistingRequestRoute(string parish)
    {
        await using var fixture = await Setup();
        using var response = await fixture.Client.GetAsync("/Management/Requests?parish=" + Uri.EscapeDataString(parish), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal(parish, fixture.Service.LastFilter?.Parish);
    }
}
