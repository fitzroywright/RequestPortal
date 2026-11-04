using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Common.Security.Abstractions;
using Common.Security.Authorization;
using Common.Security.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RequestPortal.Common.Security;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Persistence;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Pages.Administration;
using RequestPortal.Web.Security;
using Xunit;
using ApplicationUser = RequestPortal.Infrastructure.Identity.ApplicationUser;

namespace RequestPortal.Infrastructure.Tests;

public sealed class StaffProvisioningPageTests
{
    private const string Tenant = "257d294c-c8a7-4b86-b791-6755edc4fe0a";
    private const string Subject = "5c6e1d4e-a815-4c8e-bcd4-c7bf6902f6f7";
    private sealed record Actors(string Administrator, string Reviewer);
    private sealed class Directory : IStaffDirectoryLookup
    {
        public string? UnavailableReason => null;
        public string ConfigurationKey => "approved-test-directory";
        public DirectoryStaffIdentity Identity { get; } = new(Subject, "new.staff@example.test", "Provisioned Staff",
            "new.staff@example.test", true, new("staff", Tenant, Subject), AuthenticationProviderKind.EntraId);
        public Task<IReadOnlyList<DirectoryStaffIdentity>> SearchAsync(string query, CancellationToken token) => Task.FromResult<IReadOnlyList<DirectoryStaffIdentity>>([Identity]);
        public Task<DirectoryStaffIdentity?> FindAsync(string id, CancellationToken token) => Task.FromResult<DirectoryStaffIdentity?>(id == Subject ? Identity : null);
    }
    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder, Actors actors) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string actor = Request.Headers["X-Test-Actor"].ToString();
            if (actor is not ("administrator" or "reviewer" or "public")) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, actor == "reviewer" ? actors.Reviewer : actors.Administrator) };
            if (actor != "public")
            {
                claims.Add(new(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue));
                claims.Add(new(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.Access));
                if (actor == "administrator") claims.Add(new(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.ManageSecurity));
            }
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public required PostgreSqlTestDatabase Database;
        public required RequestPortalAccounts Accounts;
        public required IHost Host;
        public required HttpClient Client;
        public readonly Dictionary<string, string> Cookies = [];
        public async ValueTask DisposeAsync() { Client.Dispose(); Host.Dispose(); await Database.DisposeAsync(); }
        public async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            if (Cookies.Count > 0) request.Headers.Add("Cookie", string.Join("; ", Cookies.Select(x => x.Key + "=" + x.Value)));
            var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
                foreach (var value in values)
                {
                    var pair = value.Split(';', 2)[0].Split('=', 2);
                    if (pair.Length == 2) Cookies[pair[0]] = pair[1];
                }
            return response;
        }
        public Task<HttpResponseMessage> Get(string url) => Send(new(HttpMethod.Get, url));
        public Task<HttpResponseMessage> Post(string url, Dictionary<string, string> fields) => Send(new(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(fields) });
    }
    private static async Task<Fixture> Setup()
    {
        var database = await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        string connection = database.Db.Database.GetConnectionString()!;
        var accounts = new RequestPortalAccounts(connection); accounts.EnsureSchema();
        var roles = new RequestPortalAuthorizationStore(database.Db, Options.Create(new ActiveDirectoryOptions()));
        var service = new StaffAdministrationService(accounts, database.Db, roles, new PermissionAuthorizer(roles));
        static VerifiedIdentity Proof(string subject) => new(new("staff", Tenant, subject), AuthenticationProviderKind.EntraId,
            subject, subject, DateTimeOffset.UtcNow, "validated-fixture");
        var admin = accounts.Staff.Resolve(Proof("admin"), AccountPopulation.Staff, true).Account;
        await service.BootstrapAsync(admin.Id, "staff", Tenant, "admin", "test-operator", "verified fixture identity");
        var reviewer = accounts.Staff.Resolve(Proof("reviewer"), AccountPopulation.Staff, true).Account;
        accounts.Staff.SetStatus(reviewer.Id, SecurityAccountStatus.Active, "test-admin");
        await database.Db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "AspNetUserRoles"("UserId","RoleId") SELECT {0},"Id" FROM "AspNetRoles" WHERE "Name"='Reviewer'
            """, [Guid.Parse(reviewer.Id)], TestContext.Current.CancellationToken);
        string root = Path.GetFullPath("../../../../../RequestPortal.Web", AppContext.BaseDirectory);
        var host = new HostBuilder().ConfigureWebHost(webHost => webHost.UseTestServer().UseEnvironment("Test").UseContentRoot(root).UseWebRoot("wwwroot")
            .UseSetting(WebHostDefaults.ApplicationKey, typeof(StaffModel).Assembly.GetName().Name)
            .ConfigureServices(services =>
            {
                services.AddDbContext<RequestPortalDbContext>(options => options.UseNpgsql(connection));
                services.AddIdentityCore<ApplicationUser>().AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<RequestPortalDbContext>();
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                services.AddSingleton(accounts); services.AddSingleton(new Actors(admin.Id, reviewer.Id));
                services.AddSingleton<TimeProvider>(TimeProvider.System); services.AddSingleton<IStaffDirectoryLookup, Directory>();
                services.Configure<ActiveDirectoryOptions>(_ => { });
                services.AddScoped<IAuthorizationStore, RequestPortalAuthorizationStore>();
                services.AddScoped<IPermissionAuthorizer, PermissionAuthorizer>();
                services.AddScoped<StaffAdministrationService>(); services.AddScoped<StaffPreProvisioningService>();
                services.AddAuthentication(StaffOidcOptions.StaffCookieScheme)
                    .AddScheme<AuthenticationSchemeOptions, Authentication>(StaffOidcOptions.StaffCookieScheme, _ => { });
                services.AddAuthorization(options => options.AddPolicy("StaffAdministration", policy =>
                {
                    policy.AuthenticationSchemes.Add(StaffOidcOptions.StaffCookieScheme);
                    policy.RequireAuthenticatedUser();
                    policy.RequireClaim(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue);
                    policy.RequireClaim(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.Access);
                    policy.RequireClaim(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.ManageSecurity);
                }));
                services.AddRazorPages(options => options.Conventions.AuthorizeFolder("/Administration", "StaffAdministration"))
                    .AddApplicationPart(typeof(StaffModel).Assembly);
            }).Configure(app =>
            {
                app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapRazorPages());
            })).Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var server = host.GetTestServer();
        var client = server.CreateClient(); client.BaseAddress = new("https://localhost");
        client.DefaultRequestHeaders.Add("X-Test-Actor", "administrator");
        return new() { Database = database, Accounts = accounts, Host = host, Client = client };
    }
    private static string Field(string html, string name)
    {
        var match = Regex.Match(html, "name=\"" + Regex.Escape(name) + "\"[^>]*value=\"([^\"]*)\"");
        Assert.True(match.Success, "Expected form field " + name);
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    [Theory]
    [InlineData("Reviewer", true)]
    [InlineData("Administrator", true)]
    [InlineData("Reviewer", false)]
    public async Task SuccessfulProvisionRedirectsToRenderedDetailsWithHistoryAndOneIdentity(string role, bool activate)
    {
        await using var f = await Setup();
        var initial = await f.Get("/Administration/AddStaff"); Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        string html = await initial.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var search = await f.Post("/Administration/AddStaff?handler=Search", new() { ["Query"] = "new", ["__RequestVerificationToken"] = Field(html, "__RequestVerificationToken") });
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        html = await search.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        string selection = Field(html, "Selection");
        var review = await f.Post("/Administration/AddStaff?handler=Review", new() { ["Selection"] = selection, ["__RequestVerificationToken"] = Field(html, "__RequestVerificationToken") });
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        html = await review.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var fields = new Dictionary<string, string> { ["Selection"] = selection, ["SelectedRoles"] = role, ["Activate"] = activate ? "true" : "false", ["__RequestVerificationToken"] = Field(html, "__RequestVerificationToken") };
        var provision = await f.Post("/Administration/AddStaff?handler=Provision", fields);
        Assert.Equal(HttpStatusCode.Redirect, provision.StatusCode);
        string location = provision.Headers.Location!.OriginalString;
        Assert.StartsWith("/Administration/Staff?", location);
        string id = QueryHelpers.ParseQuery(new Uri(f.Client.BaseAddress!, location).Query)["Id"].ToString();
        Assert.True(Guid.TryParse(id, out _));
        var details = await f.Get(location); Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        html = await details.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Staff account provisioned and audited.", html);
        Assert.Contains("Staff details", html); Assert.Contains("Provisioned Staff", html);
        Assert.Contains("Never signed in", html); Assert.Contains("Staff provisioned before first sign-in", html);
        Assert.Contains(Tenant, html); Assert.Contains(Subject, html);
        Assert.Contains(activate ? "Can " : "No active access.", html);
        Assert.DoesNotContain("We couldn", html);
        Assert.Equal(activate ? SecurityAccountStatus.Active : SecurityAccountStatus.PendingAccess, f.Accounts.Staff.Find(id)!.Status);
        Assert.Equal(new ExternalIdentityKey("staff", Tenant, Subject), Assert.Single(f.Accounts.Staff.GetIdentities(id)).Key);
        fields["__RequestVerificationToken"] = Field(html, "__RequestVerificationToken");
        var repeat = await f.Post("/Administration/AddStaff?handler=Provision", fields);
        Assert.Equal(location, repeat.Headers.Location!.OriginalString);
        Assert.Equal(3, f.Accounts.Staff.List().Count);
        long events = await f.Database.Db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM requestportal_staff_admin_events WHERE action='PreProvisioned'")
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, events);
    }

    [Theory]
    [InlineData("reviewer", HttpStatusCode.Forbidden)]
    [InlineData("public", HttpStatusCode.Forbidden)]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    public async Task NonAdministratorsCannotProvisionOrDisplayStaffDetails(string actor, HttpStatusCode expected)
    {
        await using var f = await Setup();
        f.Client.DefaultRequestHeaders.Remove("X-Test-Actor"); f.Client.DefaultRequestHeaders.Add("X-Test-Actor", actor);
        Assert.Equal(expected, (await f.Get("/Administration/AddStaff")).StatusCode);
        Assert.Equal(expected, (await f.Get("/Administration/Staff?Id=" + f.Accounts.Staff.List()[0].Id)).StatusCode);
        Assert.Equal(expected, (await f.Post("/Administration/AddStaff?handler=Provision", new())).StatusCode);
        Assert.Equal(2, f.Accounts.Staff.List().Count);
    }
}
