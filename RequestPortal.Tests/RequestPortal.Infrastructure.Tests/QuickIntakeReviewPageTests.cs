using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
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
using RequestPortal.Application.Delivery;
using RequestPortal.Application.Documents;
using RequestPortal.Application.Messages;
using RequestPortal.Application.Portal;
using RequestPortal.Application.ReferenceData;
using RequestPortal.Application.Requests;
using RequestPortal.Common.Security;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Pages.Staff;
using RequestPortal.Web.Security;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class QuickIntakeReviewPageTests
{
    private static readonly Guid RequestId = Guid.Parse("77777777-7777-4777-8777-777777777777");
    private static readonly Guid StaffId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static QuickIntakeView Intake => new(RequestId, "Marsha", "Campbell", new(1986, 7, 12),
        "Female", "Married", "+18765550101", "marsha@example.test", "12 Example Lane", "Example District",
        "St. Catherine", "Self-employed", 12500.75m, true, true, ["PATH", "CARE"], "Improve My Home",
        new(2026, 10, 20, 14, 30, 0, TimeSpan.Zero), null, null, "+18765550102", "House Fire; Abuse (Physical, Emotional, Sexual, Verbal, or Substance)");

    // Only explicitly configured read operations are supported; no database or production services are used.
    public class ReadProxy : DispatchProxy
    {
        public Func<MethodInfo, object?> Read { get; set; } = _ => throw new NotSupportedException();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Read(method!);
        public static T For<T>(Func<MethodInfo, object?> read) where T : class
        {
            var proxy = Create<T, ReadProxy>(); ((ReadProxy)(object)proxy).Read = read; return proxy;
        }
    }
    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string actor = Request.Headers["X-Test-Actor"].ToString();
            if (actor == "anonymous") return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, StaffId.ToString()), new(ClaimTypes.Name, "Review fixture staff") };
            if (actor != "public") claims.Add(new(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue));
            if (actor != "no-access") claims.Add(new(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.Access));
            if (actor != "no-review") claims.Add(new(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.ReviewRequests));
            if (actor is "Administrator" or "Reviewer") claims.Add(new(ClaimTypes.Role, actor));
            return Task.FromResult(AuthenticateResult.Success(new(new(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public required IHost Host;
        public required HttpClient Client;
        public int IntakeReads;
        public async ValueTask DisposeAsync() { Client.Dispose(); await Host.StopAsync(); Host.Dispose(); }
        public async Task<string> Page()
        {
            using var response = await Client.GetAsync($"/Staff/Review/{RequestId}", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        }
    }
    private static async Task<Fixture> Setup(QuickIntakeView? intake, string actor = "Reviewer", bool requestExists = true)
    {
        Fixture fixture = null!;
        var request = new RequestDetail(RequestId, "RP-FIXTURE-001", Guid.NewGuid(), "Review fixture context", "Submitted", "Fixture request", [], []);
        string root = Path.GetFullPath("../../../../../RequestPortal.Web", AppContext.BaseDirectory);
        var host = new HostBuilder().ConfigureWebHost(web => web.UseTestServer().UseEnvironment("Test")
            .UseContentRoot(root).UseWebRoot("wwwroot").UseSetting(WebHostDefaults.ApplicationKey, typeof(ReviewModel).Assembly.GetName().Name)
            .ConfigureServices(services =>
            {
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                services.AddIdentityCore<ApplicationUser>();
                services.AddSingleton(ReadProxy.For<IUserStore<ApplicationUser>>(method => method.Name switch
                {
                    "FindByIdAsync" => Task.FromResult<ApplicationUser?>(new() { Id = StaffId, DisplayName = "Review fixture staff" }),
                    "Dispose" => null, _ => throw new NotSupportedException(method.Name)
                }));
                services.AddSingleton(ReadProxy.For<IPortalWorkflowService>(_ => Task.FromResult<RequestDetail?>(requestExists ? request : null)));
                services.AddSingleton(ReadProxy.For<IProjectProposalService>(_ => Task.FromResult<ProjectProposalDetail?>(null)));
                services.AddSingleton(ReadProxy.For<IProjectPlanningService>(_ => throw new InvalidOperationException("No project proposal in this fixture.")));
                services.AddSingleton(ReadProxy.For<IDocumentService>(_ => Task.FromResult<IReadOnlyList<DocumentSummary>>([])));
                services.AddSingleton(ReadProxy.For<IDeliveryService>(_ => Task.FromResult<IReadOnlyList<DeliveryView>>([])));
                services.AddSingleton(ReadProxy.For<IRequestReferenceDataService>(_ => Task.FromResult<IReadOnlyList<UnitOfMeasureOption>>([])));
                services.AddSingleton(ReadProxy.For<IPortalMessageService>(_ => Task.FromResult<IReadOnlyList<MessageThreadView>>([])));
                services.AddSingleton(ReadProxy.For<IQuickIntakeService>(method =>
                {
                    if (method.Name != nameof(IQuickIntakeService.GetAsync)) throw new InvalidOperationException("Unexpected write: " + method.Name);
                    fixture.IntakeReads++; return Task.FromResult(intake);
                }));
                services.AddAuthentication(StaffOidcOptions.StaffCookieScheme)
                    .AddScheme<AuthenticationSchemeOptions, Authentication>(StaffOidcOptions.StaffCookieScheme, _ => { });
                services.AddAuthorization(options => options.AddPolicy(PortalPolicies.FfpStaff, policy =>
                {
                    policy.AuthenticationSchemes.Add(StaffOidcOptions.StaffCookieScheme);
                    policy.RequireAuthenticatedUser();
                    policy.RequireClaim(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue);
                    policy.RequireClaim(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.Access);
                }));
                services.AddRazorPages(options => options.Conventions.AuthorizeFolder("/Staff", PortalPolicies.FfpStaff))
                    .AddApplicationPart(typeof(ReviewModel).Assembly);
            }).Configure(app =>
            {
                app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapRazorPages());
            })).Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var client = host.GetTestServer().CreateClient(); client.BaseAddress = new("https://localhost");
        client.DefaultRequestHeaders.Add("X-Test-Actor", actor);
        fixture = new() { Host = host, Client = client }; return fixture;
    }
    private static string Field(string html, string label)
    {
        var match = Regex.Match(html, "<dt>" + Regex.Escape(label) + "</dt><dd>(.*?)</dd>", RegexOptions.Singleline);
        Assert.True(match.Success, "Missing field: " + label); return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
    private static void Snapshot(string name, string html)
    {
        // Optional real-Razor HTML output for the local browser layout check; all values are synthetic.
        string? output = Environment.GetEnvironmentVariable("REQUESTPORTAL_REVIEW_RENDER_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, name + ".html"), html);
    }

    [Theory]
    [InlineData("Administrator")]
    [InlineData("Reviewer")]
    public async Task ReviewPermissionRendersEveryCapturedFieldAndSeparatesConversionForm(string actor)
    {
        var intake = Intake;
        await using var fixture = await Setup(intake, actor);
        string html = await fixture.Page();
        var fields = new Dictionary<string, string>
        {
            ["First name"] = intake.FirstName, ["Last name"] = intake.LastName,
            ["Date of birth"] = "1986-07-12", ["Gender"] = intake.Gender, ["Marital status"] = intake.MaritalStatus,
            ["Primary telephone / WhatsApp"] = intake.Telephone, ["Secondary telephone"] = intake.SecondaryTelephone!,
            ["Email"] = intake.Email!, ["Street address"] = intake.StreetAddress,
            ["Community / district"] = intake.CommunityDistrict, ["Parish"] = intake.Parish,
            ["Employment status"] = intake.EmploymentStatus, ["Monthly household income"] = intake.MonthlyIncome.ToString("N2"),
            ["Children under 18 living full-time"] = "Yes", ["Disability or serious medical condition"] = "Yes",
            ["Assistance programmes"] = "PATH; CARE", ["Assistance category"] = intake.HelpCategory,
            ["Victim / abuse circumstances"] = intake.VictimCircumstances!,
            ["Date received"] = intake.CreatedUtc.ToLocalTime().ToString("f"), ["Conversion status"] = "Not converted"
        };
        foreach (var (label, value) in fields) Assert.Equal(value, Field(html, label));
        Assert.Equal(1, fixture.IntakeReads);
        Assert.DoesNotContain("Weekly household income", html);
        Assert.True(html.IndexOf("class=\"quick-intake-conversion\"", StringComparison.Ordinal) > html.IndexOf("<dt>Conversion status</dt>", StringComparison.Ordinal));
        Assert.Contains("?handler=ConvertQuickIntake", html); Assert.Contains("name=\"ConversionEmail\"", html);
        Assert.Contains("name=\"__RequestVerificationToken\"", html);
        Snapshot("full", html);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingOptionalAndLegacyEmptyValuesAreNotInvented(string? absent)
    {
        await using var fixture = await Setup(Intake with { Email = absent, SecondaryTelephone = absent,
            VictimCircumstances = absent, AssistancePrograms = [], MonthlyIncome = 0,
            HasChildrenUnder18LivingFullTime = false, HasDisabilityOrSeriousMedicalCondition = false });
        string html = await fixture.Page();
        foreach (string field in new[] { "Email", "Secondary telephone", "Victim / abuse circumstances", "Assistance programmes" })
            Assert.Equal("Not provided", Field(html, field));
        Assert.Equal("0.00", Field(html, "Monthly household income"));
        Assert.Equal("No", Field(html, "Children under 18 living full-time"));
        Assert.Equal("No", Field(html, "Disability or serious medical condition"));
        Snapshot("missing", html);
    }

    [Fact]
    public async Task ExplicitNoneAnswersArePreserved()
    {
        await using var fixture = await Setup(Intake with { AssistancePrograms = ["None of the Above"], VictimCircumstances = "None of the Above" });
        string html = await fixture.Page();
        Assert.Equal("None of the Above", Field(html, "Assistance programmes"));
        Assert.Equal("None of the Above", Field(html, "Victim / abuse circumstances"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConvertedIntakesShowStatusAndAvailableTimestampWithoutConversionForm(bool hasDate)
    {
        DateTimeOffset? date = hasDate ? new(2026, 10, 22, 15, 0, 0, TimeSpan.Zero) : null;
        await using var fixture = await Setup(Intake with { ConvertedToUserId = Guid.NewGuid(), ConvertedUtc = date });
        string html = await fixture.Page();
        Assert.Equal("Converted to Individual", Field(html, "Conversion status"));
        Assert.Equal(date?.ToLocalTime().ToString("f") ?? "Not provided", Field(html, "Date converted"));
        Assert.DoesNotContain("?handler=ConvertQuickIntake", html); Snapshot("converted", html);
    }

    [Fact]
    public async Task RegularRequestWithoutIntakeStillRenders()
    {
        await using var fixture = await Setup(null);
        string html = await fixture.Page();
        Assert.Contains("RP-FIXTURE-001", html); Assert.Contains("Supporting documents", html);
        Assert.DoesNotContain("quick-intake-heading", html); Snapshot("regular", html);
    }

    [Fact]
    public async Task IntakeTextIsHtmlEncoded()
    {
        await using var fixture = await Setup(Intake with { VictimCircumstances = "<script>alert('unsafe')</script>", StreetAddress = "<b>Address</b>" });
        string html = await fixture.Page();
        Assert.Contains("&lt;script&gt;", html); Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;b&gt;Address&lt;/b&gt;", html);
    }

    [Fact]
    public async Task StaffWithoutReviewPermissionCannotReadIntakeOrConvertIt()
    {
        await using var fixture = await Setup(Intake, "no-review");
        string html = await fixture.Page();
        Assert.DoesNotContain("quick-intake-heading", html); Assert.DoesNotContain(Intake.Telephone, html);
        Assert.DoesNotContain(Intake.VictimCircumstances!, html); Assert.Equal(0, fixture.IntakeReads);
        // Exercise the handler guard before user lookup, validation, or any conversion write.
        var page = new ReviewModel(null!, null!, null!, null!, null!, null!, null!, null!)
        {
            PageContext = new() { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = new(new ClaimsIdentity([
                new(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue),
                new(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.Access)], "fixture")) } }
        };
        Assert.IsType<Microsoft.AspNetCore.Mvc.ForbidResult>(await page.OnPostConvertQuickIntakeAsync(RequestId));
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("public", HttpStatusCode.Forbidden)]
    [InlineData("no-access", HttpStatusCode.Forbidden)]
    public async Task UnauthorizedSurfacesNeverReceiveIntake(string actor, HttpStatusCode expected)
    {
        await using var fixture = await Setup(Intake, actor);
        using var response = await fixture.Client.GetAsync($"/Staff/Review/{RequestId}", TestContext.Current.CancellationToken);
        Assert.Equal(expected, response.StatusCode); Assert.Equal(0, fixture.IntakeReads);
        Assert.DoesNotContain(Intake.Telephone, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvalidConversionRedisplaysDetailsAndValidationWithoutWriting()
    {
        await using var fixture = await Setup(Intake);
        using var get = await fixture.Client.GetAsync($"/Staff/Review/{RequestId}", TestContext.Current.CancellationToken);
        string initial = await get.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var token = Regex.Match(initial, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(token.Success);
        fixture.Client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", get.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0])));
        using var response = await fixture.Client.PostAsync($"/Staff/Review/{RequestId}?handler=ConvertQuickIntake",
            new FormUrlEncodedContent(new Dictionary<string, string> {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value), ["ConversionEmail"] = ""
            }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Enter the email address of the Individual portal account.", html);
        Assert.Equal(Intake.Telephone, Field(html, "Primary telephone / WhatsApp"));
        Assert.Equal(Intake.VictimCircumstances, Field(html, "Victim / abuse circumstances"));
        Assert.Equal(2, fixture.IntakeReads);
    }

    [Fact]
    public async Task MissingRequestDoesNotLoadOrRenderIntake()
    {
        await using var fixture = await Setup(Intake, requestExists: false);
        using var response = await fixture.Client.GetAsync($"/Staff/Review/{RequestId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); Assert.Equal(0, fixture.IntakeReads);
    }
}
