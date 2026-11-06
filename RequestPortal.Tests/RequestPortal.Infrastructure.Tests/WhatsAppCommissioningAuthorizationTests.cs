using System.Net;
using Common.Security.Abstractions;
using Common.Security.Authorization;
using RequestPortal.Common.Security;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Security;
using RequestPortal.Web.Services.WhatsAppCommissioning;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class WhatsAppCommissioningAuthorizationTests
{
    [Theory]
    [InlineData("anonymous",401)]
    [InlineData("representative",403)]
    [InlineData("reviewer",403)]
    [InlineData("admin",400)]
    [InlineData("privileged-non-admin",403)]
    [InlineData("group-admin",400)]
    public async Task AdministratorPolicyAndAntiforgeryBlockUnauthorizedPostsBeforeAnyAction(string actor,int status)
    {
        var assembly=typeof(RequestPortal.Web.Pages.Administration.WhatsAppSettingsModel).Assembly;
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions {ApplicationName=assembly.GetName().Name,
            ContentRootPath=Path.GetFullPath("../../../../../RequestPortal.Web",AppContext.BaseDirectory),EnvironmentName="Test"});
        builder.WebHost.UseTestServer();builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IAuthorizationStore, Roles>();
        builder.Services.AddAuthentication(StaffOidcOptions.StaffCookieScheme).AddScheme<AuthenticationSchemeOptions,Authentication>(StaffOidcOptions.StaffCookieScheme,_=>{});
        assembly.GetType("RequestPortal.Web.Startup.AuthorizationServiceRegistration")!.GetMethod("Add")!.Invoke(null,[builder]);
        builder.Services.AddSingleton<CommissioningService>(_=>throw new InvalidOperationException("Action must not execute."));
        builder.Services.AddSingleton<StaffAdministrationService>(_=>throw new InvalidOperationException("Model must not execute."));
        await using var app=builder.Build();app.UseAuthentication();app.UseAuthorization();app.MapRazorPages();
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client=app.GetTestClient();
        foreach (string page in new[] { "WhatsAppSettings", "WhatsAppCommissioning" })
        {
            if (status is 401 or 403)
            {
                using var get = new HttpRequestMessage(HttpMethod.Get,"/Administration/"+page);
                get.Headers.Add("X-Fixture-Actor",actor);
                Assert.Equal((HttpStatusCode)status,(await client.SendAsync(get,TestContext.Current.CancellationToken)).StatusCode);
            }
            foreach (string handler in new[] {"Check","Authorize","Send"})
            {
                using var request=new HttpRequestMessage(HttpMethod.Post,"/Administration/"+page+"?handler="+handler) {Content=new FormUrlEncodedContent(new Dictionary<string,string>{{"Confirmation","SEND HELLO_WORLD"}})};
                request.Headers.Add("X-Fixture-Actor",actor);
                Assert.Equal((HttpStatusCode)status,(await client.SendAsync(request,TestContext.Current.CancellationToken)).StatusCode);
            }
        }
    }
    [Fact]
    public async Task RealCookiesChallengeAnonymousAndReturn403ForStaffAndPublicNonAdministrators()
    {
        var assembly=typeof(RequestPortal.Web.Pages.Administration.WhatsAppSettingsModel).Assembly;
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions {ApplicationName=assembly.GetName().Name,
            ContentRootPath=Path.GetFullPath("../../../../../RequestPortal.Web",AppContext.BaseDirectory),EnvironmentName="Test"});
        builder.WebHost.UseTestServer();builder.Logging.ClearProviders();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddSingleton<IAuthorizationStore,Roles>();
        builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ApplicationScheme)
            .AddCookie(StaffOidcOptions.StaffCookieScheme,o=> {o.LoginPath="/Account/StaffLogin";o.AccessDeniedPath="/Account/StaffAccessDenied";});
        assembly.GetType("RequestPortal.Web.Startup.AuthorizationServiceRegistration")!.GetMethod("Add")!.Invoke(null,[builder]);
        builder.Services.AddAuthorization(o=>o.AddPolicy("FixtureOther",p=> {p.AuthenticationSchemes.Add(StaffOidcOptions.StaffCookieScheme);p.RequireRole("MissingRole");}));
        await using var app=builder.Build();app.UseAuthentication();app.UseAuthorization();app.MapRazorPages();
        app.MapGet("/fixture/other",()=>"unreachable").RequireAuthorization("FixtureOther");
        app.MapGet("/fixture/signin/{population}",async (HttpContext context,string population)=> {
            string scheme=population=="staff"?StaffOidcOptions.StaffCookieScheme:IdentityConstants.ApplicationScheme;
            var claims=new List<Claim>{new(ClaimTypes.NameIdentifier,"privileged-non-admin"),
                new(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.Access),new(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.ManageSecurity)};
            if(population=="staff")claims.Add(new(StaffOidcOptions.StaffSurfaceClaim,StaffOidcOptions.StaffSurfaceValue));
            await context.SignInAsync(scheme,new ClaimsPrincipal(new ClaimsIdentity(claims,scheme)));
        }).AllowAnonymous();
        await app.StartAsync(TestContext.Current.CancellationToken);
        var anonymous=app.GetTestClient();
        using var challenge=await anonymous.GetAsync("/Administration/WhatsAppSettings",TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect,challenge.StatusCode);Assert.Contains("/Account/StaffLogin",challenge.Headers.Location!.OriginalString);
        foreach(string population in new[]{"staff","public"})
        {
            using var client=app.GetTestClient();
            using var signin=await client.GetAsync("/fixture/signin/"+population,TestContext.Current.CancellationToken);
            client.DefaultRequestHeaders.Add("Cookie",string.Join("; ",signin.Headers.GetValues("Set-Cookie").Select(x=>x.Split(';')[0])));
            foreach(string path in new[]{"/Administration/WhatsAppSettings","/Administration/WhatsAppCommissioning"})
            {
                using var denied=await client.GetAsync(path,TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);Assert.Null(denied.Headers.Location);
            }
            if(population=="staff")
            {
                using var unchanged=await client.GetAsync("/fixture/other",TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.Redirect,unchanged.StatusCode);Assert.Contains("AccessDenied",unchanged.Headers.Location!.OriginalString);
            }
        }
    }
    private sealed class Roles : IAuthorizationStore
    {
        public Task<IReadOnlyCollection<string>> GetDirectRoleKeysAsync(string subjectId,CancellationToken ct=default) =>
            Task.FromResult<IReadOnlyCollection<string>>(subjectId=="admin"?[PortalRoles.Administrator]:[PortalRoles.Reviewer]);
        public Task<IReadOnlyCollection<string>> GetRoleKeysForGroupsAsync(IReadOnlyCollection<string> names,CancellationToken ct=default) =>
            Task.FromResult<IReadOnlyCollection<string>>(names.Contains("fixture-admins")?[PortalRoles.Administrator]:[]);
        public Task<IReadOnlyCollection<RoleDefinition>> GetRolesAsync(IReadOnlyCollection<string> keys,CancellationToken ct=default) => throw new NotSupportedException();
    }

    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options,ILoggerFactory logger,UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string actor=Request.Headers["X-Fixture-Actor"].ToString();
            if(actor=="anonymous")return Task.FromResult(AuthenticateResult.NoResult());
            var claims=new List<Claim>{new(ClaimTypes.NameIdentifier,actor)};
            if(actor is "admin" or "reviewer" or "privileged-non-admin" or "group-admin")claims.Add(new(StaffOidcOptions.StaffSurfaceClaim,StaffOidcOptions.StaffSurfaceValue));
            claims.Add(new(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.Access));
            if(actor!="reviewer")claims.Add(new(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.ManageSecurity));
            if(actor=="privileged-non-admin")claims.Add(new(ClaimTypes.Role,PortalRoles.Administrator)); // A forged/stale role claim is insufficient.
            if(actor=="group-admin")claims.Add(new("directory_group","fixture-admins"));
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims,Scheme.Name)),Scheme.Name)));
        }
    }
}
