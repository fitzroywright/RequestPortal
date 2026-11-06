using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
    public async Task AdministratorPolicyAndAntiforgeryBlockUnauthorizedPostsBeforeAnyAction(string actor,int status)
    {
        var assembly=typeof(RequestPortal.Web.Pages.Administration.WhatsAppCommissioningModel).Assembly;
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions {ApplicationName=assembly.GetName().Name,
            ContentRootPath=Path.GetFullPath("../../../../../RequestPortal.Web",AppContext.BaseDirectory),EnvironmentName="Test"});
        builder.WebHost.UseTestServer();builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(StaffOidcOptions.StaffCookieScheme).AddScheme<AuthenticationSchemeOptions,Authentication>(StaffOidcOptions.StaffCookieScheme,_=>{});
        assembly.GetType("RequestPortal.Web.Startup.AuthorizationServiceRegistration")!.GetMethod("Add")!.Invoke(null,[builder]);
        builder.Services.AddSingleton<CommissioningService>(_=>throw new InvalidOperationException("Action must not execute."));
        builder.Services.AddSingleton<StaffAdministrationService>(_=>throw new InvalidOperationException("Model must not execute."));
        await using var app=builder.Build();app.UseAuthentication();app.UseAuthorization();app.MapRazorPages();
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client=app.GetTestClient();
        using var request=new HttpRequestMessage(HttpMethod.Post,"/Administration/WhatsAppCommissioning?handler=Send") {Content=new FormUrlEncodedContent(new Dictionary<string,string>{{"Confirmation","SEND HELLO_WORLD"}})};
        request.Headers.Add("X-Fixture-Actor",actor);
        Assert.Equal((HttpStatusCode)status,(await client.SendAsync(request,TestContext.Current.CancellationToken)).StatusCode);
    }
    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options,ILoggerFactory logger,UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string actor=Request.Headers["X-Fixture-Actor"].ToString();
            if(actor=="anonymous")return Task.FromResult(AuthenticateResult.NoResult());
            var claims=new List<Claim>{new(ClaimTypes.NameIdentifier,Guid.NewGuid().ToString())};
            if(actor is "admin" or "reviewer")claims.Add(new(StaffOidcOptions.StaffSurfaceClaim,StaffOidcOptions.StaffSurfaceValue));
            claims.Add(new(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.Access));
            if(actor!="reviewer")claims.Add(new(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.ManageSecurity));
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims,Scheme.Name)),Scheme.Name)));
        }
    }
}
