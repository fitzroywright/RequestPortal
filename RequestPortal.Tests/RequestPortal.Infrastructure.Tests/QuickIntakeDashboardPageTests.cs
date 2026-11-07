using System.Net;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Management;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Persistence;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Pages.Management;
using RequestPortal.Web.Security;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class QuickIntakeDashboardPageTests
{
    internal static QuickIntakeDashboardSnapshot Snapshot(QuickIntakeRange range)
    {
        QuickIntakeCount[] Rows(string[] names,int[] counts)=>names.Select((n,i)=>new QuickIntakeCount(n,counts[i],Math.Round(counts[i]*100m/42,1))).ToArray();
        var types=Rows(["Food","Housing","Earn an Income","Education","Health","Water & Hygiene","Not provided / unknown"],[14,9,8,6,5,0,0]);
        return new(range,42,40,Rows(JamaicaParishes.Names.Append(JamaicaParishes.Unknown).ToArray(),[2,3,4,1,2,1,3,6,0,4,1,1,2,12,0]),types,
            new(Rows(["Yes","No","Not provided / unknown"],[21,21,0]),false),new(Rows(["Employed","Self Employed","Unemployed","Student","Not provided / unknown"],[18,8,8,8,0]),false),
            new(Rows(["No income","≤ 50,000","50,000.01–100,000","100,000.01–200,000","> 200,000","Not provided / unknown"],[10,12,9,6,5,0]),false),
            new(Rows(["Male","Female","Prefer not to Say","Not provided / unknown"],[28,14,0,0]),false),new(Rows(["< 25","25–34","35–44","45–54","55–64","65+","Not provided / unknown"],[0,6,11,12,7,6,0]),false),
            ["33.3% of Quick Intakes request food assistance.","Gender: Male is the largest recorded group (66.7% of all Quick Intakes).","Age at intake: 45–54 is the largest recorded group (28.6% of all Quick Intakes).","23.8% report no income."])
        { WeeklyIncome = new(Rows(["No income", "≤ 5,000", "Over 5,000 to 10,000", "Over 10,000 to 20,000", "> 20,000", "Not provided / unknown"], [10, 8, 10, 9, 5, 0]), false) };
    }
    sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow()=>new(2026,11,5,12,0,0,TimeSpan.Zero); }
    internal sealed class Service : IQuickIntakeDashboardService
    {
        public int Calls; public QuickIntakeRange? LastRange;
        public Task<QuickIntakeDashboardSnapshot> GetAsync(QuickIntakeRange range,CancellationToken ct=default) {Calls++;LastRange=range;return Task.FromResult(Snapshot(range));}
        public Task<IReadOnlyList<QuickIntakeRecordLink>> GetRecordsAsync(QuickIntakeRange range,string? parish,int page,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<QuickIntakeRecordLink>>([]);
    }
    sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options,ILoggerFactory logger,UrlEncoder encoder):AuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string actor=Request.Headers["X-Actor"].ToString();
            if(actor=="anonymous")return Task.FromResult(AuthenticateResult.NoResult());
            var claims=new List<Claim>{new(ClaimTypes.NameIdentifier,"11111111-1111-4111-8111-111111111111"),new(ClaimTypes.Name,"Fixture staff")};
            if(actor!="public")claims.Add(new(StaffOidcOptions.StaffSurfaceClaim,StaffOidcOptions.StaffSurfaceValue));
            claims.Add(new(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.Access));
            if(actor!="no-management")claims.Add(new(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.ViewManagementDashboard));
            return Task.FromResult(AuthenticateResult.Success(new(new(new ClaimsIdentity(claims,Scheme.Name)),Scheme.Name)));
        }
    }
    internal sealed class Fixture : IAsyncDisposable
    {
        public required WebApplication App;public required HttpClient Client;internal required Service Service;
        public static async Task<Fixture> Create(string actor="Reviewer", RequestPortalDbContext? db=null)
        {
            var assembly=typeof(QuickIntakeDashboardModel).Assembly;
            var builder=WebApplication.CreateBuilder(new WebApplicationOptions{ApplicationName=assembly.GetName().Name,ContentRootPath=Path.GetFullPath("../../../../../RequestPortal.Web",AppContext.BaseDirectory),EnvironmentName="Test"});
            builder.WebHost.UseTestServer();builder.Logging.ClearProviders();builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();builder.Services.AddIdentityCore<ApplicationUser>();
            builder.Services.AddSingleton(MessageNavigationPageTests.Proxy.For<IUserStore<ApplicationUser>>((name,_)=>name=="Dispose"?null:Task.FromResult<ApplicationUser?>(new(){Id=Guid.Parse("11111111-1111-4111-8111-111111111111"),DisplayName="Fixture staff"})));
            var service=new Service();builder.Services.AddSingleton<IQuickIntakeDashboardService>(service);builder.Services.AddSingleton<TimeProvider>(new Clock());
            if(db is not null) builder.Services.AddSingleton(db);
            builder.Services.AddAuthentication(StaffOidcOptions.StaffCookieScheme).AddScheme<AuthenticationSchemeOptions,Authentication>(StaffOidcOptions.StaffCookieScheme,_=>{});
            assembly.GetType("RequestPortal.Web.Startup.AuthorizationServiceRegistration")!.GetMethod("Add")!.Invoke(null,[builder]);builder.Services.AddPageAuthorizationFixture();
            var app=builder.Build();app.UseAuthentication();app.UseAuthorization();app.MapRazorPages();await app.StartAsync(TestContext.Current.CancellationToken);
            var client=app.GetTestClient();client.DefaultRequestHeaders.Add("X-Actor",actor);return new(){App=app,Client=client,Service=service};
        }
        public async ValueTask DisposeAsync(){Client.Dispose();await App.DisposeAsync();}
    }
    [Fact]
    public async Task ReportsMenuHasExactlyFiveOrderedRoutesAndDashboardRetainsApplicationShell()
    {
        await using var f=await Fixture.Create();var ct=TestContext.Current.CancellationToken;
        using var response=await f.Client.GetAsync("/Management/QuickIntakeDashboard",ct);Assert.True(response.StatusCode == HttpStatusCode.OK,await response.Content.ReadAsStringAsync(ct));
        string html=await response.Content.ReadAsStringAsync(ct);
        string menu=Regex.Match(html,"<details class=\"staff-admin-menu reports-menu\">(.*?)</details>",RegexOptions.Singleline).Value;
        var links=Regex.Matches(menu,"<a\\b[^>]*href=\\\"([^\\\"]+)\\\"[^>]*>(.*?)</a>",RegexOptions.Singleline).Cast<Match>().ToArray();
        Assert.Equal(new[]{"Quick Intakes","Registered Users","Management","Requests","Invite Reviewers"},
            links.Select(l=>Regex.Replace(Regex.Replace(l.Groups[2].Value,"<span\\b[^>]*aria-hidden=\"true\"[^>]*>.*?</span>","",RegexOptions.Singleline),"<[^>]+>","").Trim()));
        Assert.Equal(new[]{"/Management/QuickIntakeDashboard","/Management/RegisteredUsers","/Management","/Staff/Reports","/Staff/Reviewers"},
            links.Select(l=>l.Groups[1].Value));
        Assert.Single(Regex.Matches(html,"href=\"/Management\""));Assert.Single(Regex.Matches(html,"href=\"/Staff/Reports\""));
        foreach(var title in new[]{"Quick Intake Dashboard","Requests by Location","Quick Intakes by Request Type","Children Under 18 Living at Home","Employment Status","Weekly Income (JMD)","Gender","Age Group","Key Insights"})Assert.Contains(title,html);
        Assert.Contains("id=\"qi-income-period\"", html);
        Assert.Contains("value=\"weekly\" selected", html);
        Assert.Contains("data-income-table=\"weekly\"", html);
        Assert.Matches("data-income-table=\"monthly\" hidden(?:=\"hidden\")?", html);
        Assert.Contains("Monthly Income (JMD)", html);
        Assert.Contains("site-header",html);Assert.Contains("authenticated-app",html);Assert.Contains("main-nav",html);Assert.DoesNotContain("<aside",html);
        Assert.Contains("jamaica-parishes.528b7394d3b3.geojson",html);Assert.Contains("chart.js/4.6.1",html);
        Assert.Contains("Screening not recorded",html);Assert.Contains("Account origin unavailable",html);Assert.Contains("qi-children-under-18",html);
        Assert.Equal(new QuickIntakeRange(new(2026,10,7),new(2026,11,5)),f.Service.LastRange);
        Assert.Contains("no-store",response.Headers.CacheControl!.ToString());
        string? output=Environment.GetEnvironmentVariable("REQUESTPORTAL_QI_RENDER_OUTPUT");if(output is not null){Directory.CreateDirectory(output);await File.WriteAllTextAsync(Path.Combine(output,"index.html"),html,ct);}
    }
    [Theory][InlineData("anonymous",HttpStatusCode.Unauthorized)][InlineData("public",HttpStatusCode.Forbidden)][InlineData("no-management",HttpStatusCode.Forbidden)]
    public async Task BothPageAndChartHandlerAndDrilldownEnforceExistingManagementPolicy(string actor,HttpStatusCode expected)
    {
        await using var f=await Fixture.Create(actor);var ct=TestContext.Current.CancellationToken;
        foreach(var route in new[]{"/Management/QuickIntakeDashboard","/Management/QuickIntakeDashboard?handler=Data","/Management/QuickIntakes?From=2026-11-01&Through=2026-11-05"})
        {using var response=await f.Client.GetAsync(route,ct);Assert.Equal(expected,response.StatusCode);}
        Assert.Equal(0,f.Service.Calls);
    }
    [Fact]
    public async Task AggregateEndpointUsesCustomDatesWithoutIdentifiersAndRejectsMalformedFilters()
    {
        await using var f=await Fixture.Create();var ct=TestContext.Current.CancellationToken;
        using var response=await f.Client.GetAsync("/Management/QuickIntakeDashboard?handler=Data&Period=custom&From=2026-11-01&Through=2026-11-05",ct);
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.Equal("application/json",response.Content.Headers.ContentType!.MediaType);
        string json=await response.Content.ReadAsStringAsync(ct);using var doc=JsonDocument.Parse(json);Assert.Equal(42,doc.RootElement.GetProperty("total").GetInt32());Assert.Equal(15,doc.RootElement.GetProperty("parishes").GetArrayLength());Assert.Equal(6,doc.RootElement.GetProperty("weeklyIncome").GetProperty("items").GetArrayLength());
        Assert.Equal(new QuickIntakeRange(new(2026,11,1),new(2026,11,5)),f.Service.LastRange);
        foreach(var name in new[]{"requestId","userId","firstName","telephone","dateOfBirth","mobileNumber"})Assert.DoesNotContain(name,json);
        foreach(var query in new[]{"Period=custom","Period=custom&From=bad&Through=2026-11-05","Period=custom&From=2026-11-05&Through=2026-11-01","Period=custom&From=2026-11-01&Through=2030-11-05","Period=invalid"})
        {using var invalid=await f.Client.GetAsync("/Management/QuickIntakeDashboard?handler=Data&"+query,ct);Assert.Equal(HttpStatusCode.BadRequest,invalid.StatusCode);}
    }
}
