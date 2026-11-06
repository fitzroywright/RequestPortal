using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Common.Messaging;
using Common.Security.Abstractions;
using Common.Security.Authorization;
using Common.Security.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Messaging;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Pages.Administration;
using RequestPortal.Web.Security;
using RequestPortal.Web.Services.WhatsAppCommissioning;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class WhatsAppSettingsUiTests
{
    [Fact]
    public async Task SettingsRendersExistingLayoutSectionsNavigationAndFriendlyStatusesWithoutSecrets()
    {
        await using var f=await Fixture.CreateAsync();var ct=TestContext.Current.CancellationToken;
        using var response=await f.Client.GetAsync("/Administration/WhatsAppSettings",ct);
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        string html=await response.Content.ReadAsStringAsync(ct);
        foreach(string title in new[]{"WhatsApp Settings","Connection","Configuration","Test Messaging","Delivery &amp; Diagnostics"})Assert.Contains(title,html);
        Assert.Contains("section staff-administration",html);Assert.Contains("staff-administration.css",html);
        Assert.Contains("class=\"button button-small\"",html);Assert.Contains("class=\"staff-admin-menu-panel\"",html);
        Assert.Single(Regex.Matches(html,"href=\"/Administration/WhatsAppSettings\"").Cast<Match>());
        Assert.DoesNotContain("commissioning",html,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<section class=\"card",html);
        Assert.Contains("Not checked",html);Assert.Contains("Disabled",html);
        Assert.DoesNotContain(">True<",html);Assert.DoesNotContain(">False<",html);
        Assert.Equal(0,f.Meta.Posts);
        SetCookies(f.Client,response);
        using var check=await f.Client.PostAsync("/Administration/WhatsAppSettings?handler=Check",new FormUrlEncodedContent(new Dictionary<string,string>{{"__RequestVerificationToken",Token(html)}}),ct);
        string checkedHtml=await check.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK,check.StatusCode);
        Assert.Contains("Connected",checkedHtml);Assert.Contains("Verified",checkedHtml);Assert.Contains("hello_world / en_US available",checkedHtml);
        Assert.Contains("status status-approved",checkedHtml);
        Assert.DoesNotContain("PRIVATE_SETTINGS_TOKEN",checkedHtml);
        Assert.DoesNotContain(">True<",checkedHtml);Assert.DoesNotContain(">False<",checkedHtml);
        Assert.Equal(0,f.Meta.Posts);
    }
    [Fact]
    public async Task LegacyBookmarksAndFormsUseRoleProtectedCanonicalPageAndAuthorizationNeverSends()
    {
        await using var f=await Fixture.CreateAsync();var ct=TestContext.Current.CancellationToken;
        using var get=await f.Client.GetAsync("/Administration/WhatsAppSettings",ct);
        string html=await get.Content.ReadAsStringAsync(ct);SetCookies(f.Client,get);
        using var legacyGet=await f.Client.GetAsync("/Administration/WhatsAppCommissioning",ct);
        Assert.Equal(HttpStatusCode.Redirect,legacyGet.StatusCode);
        Assert.StartsWith("/Administration/WhatsAppSettings",legacyGet.Headers.Location!.OriginalString);
        var form=new Dictionary<string,string>{{"__RequestVerificationToken",Token(html)},{"Recipient","+15555550123"},{"RepeatRecipient","+15555550123"},{"VerifiedInMeta","true"},{"AuthorizeRecipient","true"}};
        using var forward=await f.Client.PostAsync("/Administration/WhatsAppCommissioning?handler=Authorize",new FormUrlEncodedContent(form),ct);
        Assert.Equal(HttpStatusCode.TemporaryRedirect,forward.StatusCode);
        Assert.StartsWith("/Administration/WhatsAppSettings",forward.Headers.Location!.OriginalString);
        using var authorize=await f.Client.PostAsync(forward.Headers.Location,new FormUrlEncodedContent(form),ct);
        Assert.Equal(HttpStatusCode.Redirect,authorize.StatusCode);
        Assert.StartsWith("/Administration/WhatsAppSettings",authorize.Headers.Location!.OriginalString);
        using var operation=await f.Client.GetAsync(authorize.Headers.Location,ct);
        string operationHtml=await operation.Content.ReadAsStringAsync(ct);
        Assert.Contains("Ready to send once",operationHtml);Assert.Contains("SEND HELLO_WORLD",operationHtml);
        Assert.DoesNotContain("+15555550123",operationHtml);
        Assert.Equal(0,f.Meta.Posts);
    }
    [Fact]
    public async Task CurrentRoleRevocationBlocksSettingsDespiteStaleAdministratorPermissionClaims()
    {
        await using var f=await Fixture.CreateAsync();var ct=TestContext.Current.CancellationToken;
        using var allowed=await f.Client.GetAsync("/Administration/WhatsAppSettings",ct);
        Assert.Equal(HttpStatusCode.OK,allowed.StatusCode);
        await f.Database.Db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AspNetUserRoles\" WHERE \"UserId\"={f.Actor}",ct);
        foreach(var path in new[]{"/Administration/WhatsAppSettings","/Administration/WhatsAppCommissioning"})
        {
            using var denied=await f.Client.GetAsync(path,ct);Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
        }
        using var home=await f.Client.GetAsync("/",ct);
        Assert.Equal(HttpStatusCode.OK,home.StatusCode);
        Assert.DoesNotContain("href=\"/Administration/WhatsAppSettings\"",await home.Content.ReadAsStringAsync(ct));
        Assert.Equal(0,f.Meta.Posts);
    }
    [Theory]
    [InlineData(true,"Verified","status status-approved")]
    [InlineData(false,"Needs attention","status status-rejected")]
    [InlineData(null,"Not checked","status")]
    public void StatusIndicatorsAreHumanReadable(bool? value,string label,string css)
    {Assert.Equal(label,WhatsAppSettingsModel.CheckLabel(value));Assert.Equal(css,WhatsAppSettingsModel.CheckClass(value));}
    static string Token(string html)=>WebUtility.HtmlDecode(Regex.Match(html,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    static void SetCookies(HttpClient client,HttpResponseMessage response)
    {if(response.Headers.TryGetValues("Set-Cookie",out var cookies))client.DefaultRequestHeaders.Add("Cookie",string.Join("; ",cookies.Select(x=>x.Split(';')[0])));}
    sealed class Fixture : IAsyncDisposable
    {
        public required PostgreSqlTestDatabase Database; public required WebApplication App;public required HttpClient Client;public required Meta Meta;public Guid Actor;
        public static async Task<Fixture> CreateAsync()
        {
            var ct=TestContext.Current.CancellationToken;var db=await PostgreSqlTestDatabase.CreateAsync(ct);
            var accounts=new RequestPortalAccounts(db.Db.Database.GetConnectionString()!);accounts.EnsureSchema();
            var roles=new RequestPortalAuthorizationStore(db.Db,Options.Create(new ActiveDirectoryOptions()));var authorizer=new PermissionAuthorizer(roles);
            var administration=new StaffAdministrationService(accounts,db.Db,roles,authorizer);
            var proof=new VerifiedIdentity(new ExternalIdentityKey("staff-oidc","tenant","settings-admin"),AuthenticationProviderKind.EntraId,"Settings Administrator","settings-admin",DateTimeOffset.UtcNow,"validated-fixture");
            var account=accounts.Staff.Resolve(proof,AccountPopulation.Staff,true).Account;
            await administration.BootstrapAsync(account.Id,"staff-oidc","tenant","settings-admin","fixture","fixture verification");
            db.Db.ChangeTracker.Clear();
            var assembly=typeof(WhatsAppSettingsModel).Assembly;
            var builder=WebApplication.CreateBuilder(new WebApplicationOptions {ApplicationName=assembly.GetName().Name,ContentRootPath=Path.GetFullPath("../../../../../RequestPortal.Web",AppContext.BaseDirectory),EnvironmentName="Test"});
            builder.WebHost.UseTestServer();builder.Logging.ClearProviders();builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton(db.Db);builder.Services.AddSingleton(accounts);builder.Services.AddSingleton<IAuthorizationStore>(roles);builder.Services.AddSingleton<IPermissionAuthorizer>(authorizer);builder.Services.AddSingleton(administration);
            builder.Services.AddIdentityCore<RequestPortal.Infrastructure.Identity.ApplicationUser>().AddEntityFrameworkStores<RequestPortal.Infrastructure.Persistence.RequestPortalDbContext>();
            builder.Services.AddAuthentication(StaffOidcOptions.StaffCookieScheme).AddScheme<AuthenticationSchemeOptions,Authentication>(StaffOidcOptions.StaffCookieScheme,_=>{});
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();builder.Services.AddSingleton(TimeProvider.System);
            var settings=new NotificationOptions {WhatsAppWebhookEnabled=true,WhatsAppPhoneNumberId=CommissioningGateway.Phone,WhatsAppBusinessAccountId=CommissioningGateway.Account,WhatsAppGraphApiVersion="v23.0",WhatsAppAccessTokenSecretName=CommissioningGateway.TokenReference};
            builder.Services.AddSingleton(Options.Create(settings));var meta=new Meta();builder.Services.AddSingleton(new CommissioningGateway(new HttpClient(meta),new Secrets()));
            builder.Services.AddScoped<ICommissioningJournal,CommissioningJournal>();builder.Services.AddScoped<IIntegrationOperatorAuthorization,IntegrationOperatorAuthorization>();builder.Services.AddScoped<CommissioningService>();
            assembly.GetType("RequestPortal.Web.Startup.AuthorizationServiceRegistration")!.GetMethod("Add")!.Invoke(null,[builder]);
            var app=builder.Build();app.UseAuthentication();app.UseAuthorization();app.MapRazorPages();await app.StartAsync(ct);
            var client=app.GetTestClient();client.DefaultRequestHeaders.Add("X-Fixture-Actor",account.Id);
            return new(){Database=db,App=app,Client=client,Meta=meta,Actor=Guid.Parse(account.Id)};
        }
        public async ValueTask DisposeAsync(){Client.Dispose();await App.DisposeAsync();await Database.DisposeAsync();Meta.Dispose();}
    }
    sealed class Secrets : IChannelSecretResolver
    {public Task<string?> GetSecretAsync(string name,CancellationToken ct=default)=>Task.FromResult<string?>("PRIVATE_SETTINGS_TOKEN");}
    sealed class Meta : HttpMessageHandler
    {
        public int Posts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if(request.Method==HttpMethod.Post){Posts++;throw new InvalidOperationException("UI tests must never send.");}
            string body=request.RequestUri!.AbsolutePath.EndsWith("/permissions")?"{\"data\":[{\"permission\":\"whatsapp_business_messaging\",\"status\":\"granted\"}]}"
                :request.RequestUri.AbsolutePath.EndsWith("/message_templates")?"{\"data\":[{\"name\":\"hello_world\",\"language\":\"en_US\",\"status\":\"APPROVED\"}]}"
                :$"{{\"id\":\"{CommissioningGateway.Phone}\",\"code_verification_status\":\"VERIFIED\",\"platform_type\":\"CLOUD_API\"}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body,System.Text.Encoding.UTF8,"application/json")});
        }
    }
    sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options,ILoggerFactory logger,UrlEncoder encoder):AuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims=new[]{new Claim(ClaimTypes.NameIdentifier,Request.Headers["X-Fixture-Actor"].ToString()),new Claim(StaffOidcOptions.StaffSurfaceClaim,StaffOidcOptions.StaffSurfaceValue),new Claim(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.Access),new Claim(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.ManageSecurity)};
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims,Scheme.Name)),Scheme.Name)));
        }
    }
}
