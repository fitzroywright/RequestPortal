using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Common.Security.Abstractions;
using Common.Security.Authorization;
using Common.Security.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RequestPortal.Common.Security;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Messages;
using RequestPortal.Infrastructure.Persistence;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Pages.Administration;
using RequestPortal.Web.Security;
using RequestPortal.Web.Services;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class NotificationRoutingAdministrationTests
{
    [Fact]
    public async Task AdministratorCanConfigureEligibleRecipientsWithAuditAndConcurrencyProtection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PostgreSqlTestDatabase.CreateAsync(ct);
        var accounts = new RequestPortalAccounts(db.Db.Database.GetConnectionString()!); accounts.EnsureSchema();
        var roles = new RequestPortalAuthorizationStore(db.Db, Options.Create(new ActiveDirectoryOptions()));
        var permissions = new PermissionAuthorizer(roles);
        var administration = new StaffAdministrationService(accounts, db.Db, roles, permissions);
        var admin = accounts.Staff.Resolve(Proof("routing-admin"), AccountPopulation.Staff, true).Account;
        await administration.BootstrapAsync(admin.Id, "staff-oidc", "tenant", "routing-admin", "fixture", "routing verification");
        var principal = Principal(admin.Id);
        var reviewer = accounts.Staff.Resolve(Proof("routing-reviewer"), AccountPopulation.Staff, true).Account;
        await administration.ChangeAsync(principal, reviewer.Id, "Approve", [PortalRoles.Reviewer], reviewer.SessionVersion);
        db.Db.ChangeTracker.Clear();
        var access = new StaffNotificationAccess(accounts, db.Db, permissions, roles);
        var routing = new StaffNotificationRouting(db.Db, access);
        var service = new NotificationRoutingAdministration(db.Db, administration, routing, access);
        Assert.True(await access.IsEligibleAsync(Guid.Parse(reviewer.Id), ct));
        Assert.False(await access.IsAdministratorAsync(Guid.Parse(reviewer.Id), ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(Principal(reviewer.Id), Guid.Parse(reviewer.Id), true, false, "", ct));
        await service.SaveAsync(principal, Guid.Parse(reviewer.Id), true, true, "", ct);
        Assert.Equal(new[] { Guid.Parse(reviewer.Id) }, await routing.RecipientsAsync(null, false, ct));
        Assert.Equal(new[] { Guid.Parse(reviewer.Id) }, await routing.RecipientsAsync(null, true, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(principal, Guid.Parse(reviewer.Id), false, false, "", ct));
        Assert.Single(await db.Db.AuditEntries.Where(a => a.Action == "NotificationRoutingChanged").ToArrayAsync(ct));

        var assembly = typeof(NotificationRoutingModel).Assembly;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = assembly.GetName().Name,
            ContentRootPath = Path.GetFullPath("../../../../../RequestPortal.Web", AppContext.BaseDirectory), EnvironmentName = "Test" });
        builder.WebHost.UseTestServer(); builder.Logging.ClearProviders(); builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(db.Db); builder.Services.AddSingleton(accounts); builder.Services.AddSingleton<IAuthorizationStore>(roles);
        builder.Services.AddSingleton<IPermissionAuthorizer>(permissions); builder.Services.AddSingleton(administration); builder.Services.AddSingleton(service);
        builder.Services.AddIdentityCore<RequestPortal.Infrastructure.Identity.ApplicationUser>().AddEntityFrameworkStores<RequestPortalDbContext>();
        builder.Services.AddAuthentication(StaffOidcOptions.StaffCookieScheme).AddScheme<AuthenticationSchemeOptions, Authentication>(StaffOidcOptions.StaffCookieScheme, _ => { });
        assembly.GetType("RequestPortal.Web.Startup.AuthorizationServiceRegistration")!.GetMethod("Add")!.Invoke(null, [builder]);
        await using var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.MapRazorPages(); await app.StartAsync(ct);
        using var client = app.GetTestClient();
        using var anonymous = await client.GetAsync("/Administration/NotificationRouting", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Add("X-Fixture-Actor", reviewer.Id);
        using var forbidden = await client.GetAsync("/Administration/NotificationRouting", ct);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        client.DefaultRequestHeaders.Remove("X-Fixture-Actor"); client.DefaultRequestHeaders.Add("X-Fixture-Actor", admin.Id);
        using var page = await client.GetAsync("/Administration/NotificationRouting", ct);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        string html = await page.Content.ReadAsStringAsync(ct);
        Assert.Contains("Notification Routing", html); Assert.Contains("Client Relations / intake", html); Assert.Contains("Operational escalation", html);
        Assert.Contains("staff-administration.css", html);
        using var noCsrf = await client.PostAsync("/Administration/NotificationRouting", new FormUrlEncodedContent(new Dictionary<string, string>()), ct);
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        if (page.Headers.TryGetValues("Set-Cookie", out var cookies))
            client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies.Select(c => c.Split(';')[0])));
        string csrf = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        using var saved = await client.PostAsync("/Administration/NotificationRouting", new FormUrlEncodedContent(new Dictionary<string, string> {
            ["__RequestVerificationToken"] = csrf, ["StaffUserId"] = reviewer.Id, ["Version"] = "intake,operations" }), ct);
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Empty(await routing.RecipientsAsync(null, false, ct));
        // Saving an initially empty selection must bind successfully too.
        using var initial = await client.PostAsync("/Administration/NotificationRouting", new FormUrlEncodedContent(new Dictionary<string, string> {
            ["__RequestVerificationToken"] = csrf, ["StaffUserId"] = reviewer.Id, ["Version"] = "", ["Intake"] = "true", ["Operations"] = "true" }), ct);
        Assert.Equal(HttpStatusCode.Redirect, initial.StatusCode);
        Assert.Single(await routing.RecipientsAsync(null, false, ct));
        // An inactive designated identity is excluded without replacing it with administrators.
        var current = accounts.Staff.Find(reviewer.Id)!;
        await administration.ChangeAsync(principal, reviewer.Id, "Suspend", [PortalRoles.Reviewer], current.SessionVersion, true);
        Assert.Empty(await routing.RecipientsAsync(null, false, ct));
        Assert.Empty(await routing.RecipientsAsync(null, true, ct));
    }
    static VerifiedIdentity Proof(string subject) => new(new ExternalIdentityKey("staff-oidc", "tenant", subject), AuthenticationProviderKind.EntraId,
        subject, subject, DateTimeOffset.UtcNow, "validated-fixture");
    static ClaimsPrincipal Principal(string id) => new(new ClaimsIdentity(new[] {
        new Claim(ClaimTypes.NameIdentifier, id), new Claim(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue),
        new Claim(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.Access), new Claim(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.ManageSecurity) }, "fixture"));
    sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            Request.Headers["X-Fixture-Actor"].FirstOrDefault() is { } id
                ? AuthenticateResult.Success(new AuthenticationTicket(Principal(id), Scheme.Name)) : AuthenticateResult.NoResult());
    }
}
