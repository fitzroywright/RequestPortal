using RequestPortal.Application.Integration;
using RequestPortal.Domain.Integration;
using System.Net;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Administration;
using RequestPortal.Application.Delivery;
using RequestPortal.Application.Documents;
using RequestPortal.Application.Messages;
using RequestPortal.Application.Portal;
using RequestPortal.Application.ReferenceData;
using RequestPortal.Application.Requests;
using RequestPortal.Domain.Documents;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Pages.Staff;
using RequestPortal.Web.Security;
using Xunit;
using Proxy = RequestPortal.Infrastructure.Tests.MessageNavigationPageTests.Proxy;

namespace RequestPortal.Infrastructure.Tests;

public sealed class StaffSearchNavigationPageTests
{
    private static readonly Guid Staff = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Organization = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid RequestId = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid OrgDocument = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly Guid RequestDocument = Guid.Parse("55555555-5555-4555-8555-555555555555");
    private sealed class State
    {
        public int Reads, Writes, Downloads;
        public string TransferApplicant = "John Brown";
        public IntegrationTransferRow Transfer => new(RequestId,Organization,"REQ-FIXTURE",TransferApplicant,"Quick Intake","FFPManager",
            "JMDB01 / FFPJM",SynchronizationStatus.Failed,"SqlSubmission","PermanentSql","SQL_CONSTRAINT","SQL Server rejected a field value.",
            DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,1,null,null,null,null,null,true);

        public bool Archived;
        public StaffRequestSearchRow Row => new() { RequestId = RequestId, PublicReference = "REQ-FIXTURE", ApplicantName = "John Brown", OrganizationName = "Fixture organization", Category = "Food", ReceivedUtc = DateTimeOffset.UtcNow, Status = RequestStatus.Submitted };
        public IReadOnlyList<StaffRequestSearchRow> QueueRows => Enumerable.Range(0,46).Select(i=>new StaffRequestSearchRow { RequestId=i==0?RequestId:Guid.Parse($"33333333-3333-4333-8333-{i:D12}"),PublicReference=i==0?"REQ-FIXTURE":$"REQ-QUEUE-{i:D3}",ApplicantName="John Brown",OrganizationName="Fixture organization",Summary="Queue summary",ReceivedUtc=DateTimeOffset.UtcNow.AddDays(-i),CreatedUtc=DateTimeOffset.UtcNow.AddDays(-i),Status=RequestStatus.Submitted }).ToArray();
        public DocumentSummary Document(Guid id) => new(id, DocumentType.RequestSupport, "evidence.pdf", "application/pdf", 12, DateTimeOffset.UtcNow, DocumentVerificationStatus.Pending, DocumentRetentionClass.RequestEvidence, null, false, null);
        public StaffOperationsSnapshot Snapshot => new([], [new(Organization, "Fixture organization", "JM", DateOnly.FromDateTime(DateTime.UtcNow), "Approved", 0)], [], [],
            [new(OrgDocument, Organization, "Fixture organization", DocumentType.OrganizationRegistrationCertificate, "registration.pdf", DateTimeOffset.UtcNow, 1),
             new(RequestDocument, Organization, "Internal context", DocumentType.RequestSupport, "intake.pdf", DateTimeOffset.UtcNow, 1, RequestId, "Intake Applicant")], [], []);
    }
    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string actor = Request.Headers["X-Test-Actor"].ToString();
            if (actor is "anonymous" or "expired") return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Staff.ToString()), new(ClaimTypes.Name, "Fixture staff") };
            if (actor is "Administrator" or "Reviewer" or "no-access") claims.Add(new(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue));
            if (actor == "Administrator") claims.Add(new(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.ManageSecurity));
            if (actor is "Administrator" or "Reviewer") claims.Add(new(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.Access));
            return Task.FromResult(AuthenticateResult.Success(new(new(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.Redirect("/Account/StaffLogin?ReturnUrl=" + Uri.EscapeDataString(Request.Path + Request.QueryString)); return Task.CompletedTask;
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public required WebApplication App;
        public required HttpClient Client;
        public required State Data;
        private readonly Dictionary<string, string> cookies = [];
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); }
        public async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            if (cookies.Count > 0) request.Headers.Add("Cookie", string.Join("; ", cookies.Select(p => p.Key + "=" + p.Value)));
            var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var values)) foreach (var value in values) { var pair = value.Split(';', 2)[0].Split('=', 2); cookies[pair[0]] = pair[1]; }
            return response;
        }
        public Task<HttpResponseMessage> Get(string url) => Send(new(HttpMethod.Get, url));
        public async Task<HttpResponseMessage> Post(string url, Guid document, string token) => await Send(new(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(new Dictionary<string,string> { ["documentId"] = document.ToString(), ["reason"] = "Fixture rejection", ["__RequestVerificationToken"] = token }) });
    }
    private static async Task<Fixture> Setup(string actor = "Administrator")
    {
        var data = new State();
        string root = Path.GetFullPath("../../../../../RequestPortal.Web", AppContext.BaseDirectory);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = typeof(ReviewModel).Assembly.GetName().Name, ContentRootPath = root, WebRootPath = "wwwroot", EnvironmentName = "Test" });
        builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
        builder.Services.AddRateLimiter(_ => { }); builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider(); builder.Services.AddIdentityCore<ApplicationUser>();
        builder.Services.AddSingleton(Proxy.For<IUserStore<ApplicationUser>>((method, _) => method == "Dispose" ? null : Task.FromResult<ApplicationUser?>(new() { Id = Staff, DisplayName = "Fixture staff" })));
        builder.Services.AddSingleton(Proxy.For<IStaffRequestSearchService>((method, args) =>
        {
            data.Reads++;
            if (method == "GetApplicantsAsync") return Task.FromResult<IReadOnlyDictionary<Guid, StaffRequestSearchRow>>(new Dictionary<Guid, StaffRequestSearchRow> { [RequestId] = data.Row });
            if (method == "GetSubmittedQueueAsync")
            {
                int pageSize=(int)args[1]!;pageSize=pageSize is 25 or 50 or 100?pageSize:25;
                int page=Math.Clamp((int)args[0]!,1,(int)Math.Ceiling(46d/pageSize));
                return Task.FromResult(new StaffRequestSearchResult(data.QueueRows.Skip((page-1)*pageSize).Take(pageSize).ToArray(),46,page,pageSize,null));
            }
            string search = (string)args[0]!;
            int size = args.Length == 4 ? (int)args[2]! : 25;
            return Task.FromResult(search.Equals("REQ-FIXTURE", StringComparison.OrdinalIgnoreCase) ? new StaffRequestSearchResult([], 1, 1, 25, RequestId)
                : search == "none" ? new([], 0, 1, 25, null) : new([data.Row], 101, (int)args[1]!, size, null));
        }));
        builder.Services.AddSingleton(Proxy.For<IPortalWorkflowService>((method, args) =>
        {
            data.Reads++;
            return method switch
            {
                "GetRequestAsync" => Task.FromResult<RequestDetail?>((Guid)args[1]! == RequestId ? new(RequestId, "REQ-FIXTURE", Organization, "Fixture organization", "Submitted", "Fixture summary", [], []) : null),
                "GetSubmittedRequestsAsync" => throw new InvalidOperationException("The staff page must not load the unlimited submitted request list."),
                "GetPendingOrganizationsAsync" => Task.FromResult<IReadOnlyList<OrganizationSummary>>([new(Organization, "Fixture organization", "JM", "PendingApproval", "Approved", "Administrator")]),
                _ => throw new NotSupportedException(method)
            };
        }));
        builder.Services.AddSingleton(Proxy.For<IStaffOperationsService>((_, _) => { data.Reads++; return Task.FromResult(data.Snapshot); }));
        builder.Services.AddSingleton(Proxy.For<IStaffOrganizationDirectoryService>((_, _) => Task.FromResult<IReadOnlyList<RequestPortal.Application.Portal.StaffOrganizationSummary>>([new(Organization, "Fixture organization", "JM", "Approved", null, 1, 1, DateTimeOffset.UtcNow, null)])));
        builder.Services.AddSingleton(Proxy.For<IStaffOrganizationService>((_, args) => { data.Reads++; return Task.FromResult<StaffOrganizationDetail?>((Guid)args[0]! == Organization ? new(Organization, "Fixture organization", "JM", "Approved", null, null, null, null, null, null, null, DateTimeOffset.UtcNow, null, [], [], []) : null); }));
        builder.Services.AddSingleton(Proxy.For<IStaffDocumentContextService>((_, args) =>
        {
            data.Reads++;
            bool valid = (DocumentOwnerType)args[0]! == DocumentOwnerType.Organization ? (Guid)args[1]! == Organization && (Guid)args[2]! == OrgDocument : (Guid)args[1]! == RequestId && (Guid)args[2]! == RequestDocument;
            return Task.FromResult(!valid ? StaffDocumentContextStatus.NotFound : data.Archived ? StaffDocumentContextStatus.Archived : StaffDocumentContextStatus.Available);
        }));
        builder.Services.AddSingleton(Proxy.For<IDocumentService>((method, args) =>
        {
            if (method == "GetRequestDocumentsAsync") return Task.FromResult<IReadOnlyList<DocumentSummary>>([data.Document(RequestDocument)]);
            if (method == "GetOrganizationDocumentsAsync") return Task.FromResult<IReadOnlyList<DocumentSummary>>([data.Document(OrgDocument)]);
            if (method == "DownloadAsync") { data.Downloads++; return Task.FromResult<DocumentDownload?>(new(new MemoryStream("fixture document"u8.ToArray()), "application/pdf", "evidence.pdf")); }
            data.Writes++; return Task.CompletedTask;
        }));
        builder.Services.AddSingleton(Proxy.For<IProjectProposalService>((_, _) => Task.FromResult<ProjectProposalDetail?>(null)));
        builder.Services.AddSingleton(Proxy.For<IProjectPlanningService>((_, _) => throw new InvalidOperationException("No project")));
        builder.Services.AddSingleton(Proxy.For<IDeliveryService>((_, _) => Task.FromResult<IReadOnlyList<DeliveryView>>([])));
        builder.Services.AddSingleton(Proxy.For<IRequestReferenceDataService>((_, _) => Task.FromResult<IReadOnlyList<UnitOfMeasureOption>>([])));
        builder.Services.AddSingleton(Proxy.For<IQuickIntakeService>((_, _) => Task.FromResult<QuickIntakeView?>(null)));
        builder.Services.AddSingleton(Proxy.For<IIntegrationStatusService>((method,args) => {
            data.Reads++;
            return method switch {
                "CountAttentionAsync" => Task.FromResult(1),
                "GetReturnHistoryAsync" => Task.FromResult<IReadOnlyList<IntegrationReturnObservation>>([]),
                "GetTransferAsync" => Task.FromResult<IntegrationTransferRow?>((Guid)args[0]! == RequestId ? data.Transfer : null),
                "GetRequestAsync" => Task.FromResult<IntegrationTransferDetail?>((Guid)args[0]! == RequestId ? new(data.Transfer,
                    [new(Guid.NewGuid(),1,"Submission",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,false,"SqlSubmission","PermanentSql","SQL_CONSTRAINT","SQL Server rejected a field value.")],[],1) : null),
                "GetAttemptPageAsync" => Task.FromResult<IReadOnlyList<IntegrationAttemptView>>([]),
                "SearchAsync" => Task.FromResult(new IntegrationMonitorPage([data.Transfer],1,1,25,new(1,0,0,0,0))),
                "ReconcileRequestAsync" or "RetryAsync" => Write(),
                _ => throw new InvalidOperationException(method)
            };
            Task Write(){data.Writes++;return Task.CompletedTask;}
        }));
        builder.Services.AddSingleton(Proxy.For<IExternalApplicationBridgeClient>((method,_)=>method=="GetReturnOutboxAsync" ? Task.FromResult(new ExternalReturnOutboxStatus(2,1,0,DateTimeOffset.UtcNow)) : throw new InvalidOperationException(method)));
        builder.Services.AddSingleton(Proxy.For<global::Common.Storage.IFileStorage>((method,_)=>throw new InvalidOperationException(method)));
        builder.Services.AddSingleton(Proxy.For<global::Common.Diagnostics.IDiagnosticOperationsRouter>((method,_)=>throw new InvalidOperationException(method)));

        builder.Services.AddSingleton(Proxy.For<IPortalMessageService>((method, _) => method == "GetUnreadCountAsync" ? Task.FromResult(0) : Task.FromResult<IReadOnlyList<MessageThreadView>>([])));
        builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, Authentication>("fixture", _ => { })
            .AddScheme<AuthenticationSchemeOptions, Authentication>(StaffOidcOptions.StaffCookieScheme, _ => { })
            .AddScheme<AuthenticationSchemeOptions, Authentication>("ExternalReviewer", _ => { })
            .AddScheme<AuthenticationSchemeOptions, Authentication>(IdentityConstants.ApplicationScheme, _ => { });
        var assembly = typeof(ReviewModel).Assembly;
        assembly.GetType("RequestPortal.Web.Startup.AuthorizationServiceRegistration")!.GetMethod("Add")!.Invoke(null, [builder]);
        var app = builder.Build(); assembly.GetType("RequestPortal.Web.Startup.ApplicationPipeline")!.GetMethod("Configure")!.Invoke(null, [app]); app.MapRazorPages();
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestServer().CreateClient(); client.BaseAddress = new("https://localhost"); client.DefaultRequestHeaders.Add("X-Test-Actor", actor);
        return new() { App = app, Client = client, Data = data };
    }
    private static string Href(Match tag, string attribute) => WebUtility.HtmlDecode(Regex.Match(tag.Value, attribute + "=\"([^\"]*)\"").Groups[1].Value);
    private static string Token(string html) => Href(Regex.Match(html, "<input[^>]*name=\"__RequestVerificationToken\"[^>]*>"), "value");
    private static void Snapshot(string name, string html)
    {
        var directory = Environment.GetEnvironmentVariable("REQUESTPORTAL_STAFF_NAV_RENDER_OUTPUT");
        if (string.IsNullOrWhiteSpace(directory)) return; Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, name + ".html"), html);
    }

    [Theory][InlineData("Administrator")][InlineData("Reviewer")]
    public async Task SearchAndQueueRenderNamesLinksPaginationClearAndExactReference(string actor)
    {
        await using var f = await Setup(actor);
        using var queue = await f.Get("/Staff"); Assert.Equal(HttpStatusCode.OK, queue.StatusCode);
        string html = await queue.Content.ReadAsStringAsync(TestContext.Current.CancellationToken); Snapshot("queue", html);
        Assert.Contains("Showing 1", html); Assert.Contains("25 of 46 requests", html); Assert.Contains("Page 1 of 2", html); Assert.DoesNotContain("View queue", html); Assert.DoesNotContain("View all submitted requests", html);
        Assert.Contains("John Brown", html); Assert.DoesNotContain("Registered requester", html); Assert.Contains("Search by applicant name or request reference...", html);
        var orgLink = Regex.Match(html, "data-row-href=\"([^\"]*Staff/Organization[^\"]*)\""); Assert.Contains("?id=" + Organization, Href(orgLink, "data-row-href"));
        using var result = await f.Get("/Staff?Search=John&PageNumber=1"); Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        html = await result.Content.ReadAsStringAsync(TestContext.Current.CancellationToken); Snapshot("search", html);
        Assert.Contains("John Brown", html); Assert.Contains("Clear Search", html); Assert.Contains("PageNumber=2", html); Assert.Contains("Search=John", html); Assert.Contains("/Staff/Review/" + RequestId, html);
        Assert.DoesNotContain("Requests Awaiting Review", html);
        Assert.Contains("Showing 1", html); Assert.Contains("of 101 requests", html);
        using var sized = await f.Get("/Staff?Search=John&PageSize=50&PageNumber=2");
        string sizedHtml = await sized.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Showing 51", sizedHtml); Assert.Contains("100 of 101 requests", sizedHtml); Assert.Contains("Page 2 of 3", sizedHtml);
        Assert.Contains("PageSize=50", sizedHtml); Assert.Contains("Search=John", sizedHtml);
        using var search50=await f.Get("/Staff?Search=John&PageSize=50");Snapshot("search-50",await search50.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using var none = await f.Get("/Staff?Search=none"); Assert.Contains("No requests match", await none.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        foreach (string query in new[] { "Search=req-fixture", "requestCode=REQ-FIXTURE" })
        { using var exact = await f.Get("/Staff?" + query); Assert.Equal(HttpStatusCode.Redirect, exact.StatusCode); Assert.Equal("/Staff/Review/" + RequestId, exact.Headers.Location!.OriginalString); }
    }

    [Fact]
    public async Task QueueRendersEveryRowAcrossPagesAndRetainsPageSize()
    {
        await using var f=await Setup();
        foreach(var sample in new[]{(Query:"",Rows:25,Range:"25 of 46 requests",Page:"Page 1 of 2"),(Query:"?PageNumber=2&PageSize=25",Rows:21,Range:"46 of 46 requests",Page:"Page 2 of 2"),(Query:"?PageSize=50",Rows:46,Range:"46 of 46 requests",Page:"Page 1 of 1"),(Query:"?PageSize=100",Rows:46,Range:"46 of 46 requests",Page:"Page 1 of 1")})
        {
            using var response=await f.Get("/Staff"+sample.Query);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            string html=await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            var panel=Regex.Match(html,"<main class=\"workspace-panel request-panel\">(.*?)</main>",RegexOptions.Singleline).Value;
            Assert.Equal(sample.Rows,Regex.Matches(panel,"<tr class=\"staff-select-row\"").Count);
            Assert.Contains(sample.Range,panel);Assert.Contains(sample.Page,panel);Assert.Contains("Requests per page",panel);
            Snapshot(sample.Query.Contains("PageNumber=2")?"queue-page-2":sample.Query.Contains("50")?"queue-size-50":sample.Query.Contains("100")?"queue-size-100":"queue",html);
            foreach(Match row in Regex.Matches(panel,"<tr class=\"staff-select-row\".*?</tr>",RegexOptions.Singleline))
                Assert.Contains("href=\""+Href(row,"data-row-href")+"\"",row.Value);
        }
    }

    [Theory][InlineData("Administrator")][InlineData("Reviewer")]
    public async Task OrganizationRenewalAndBothDocumentRowsGenerateTheSameValidDestinationsAsActions(string actor)
    {
        await using var f = await Setup(actor);
        using var response = await f.Get("/Staff/Organizations"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken); Snapshot("organizations", html);
        var rows = Regex.Matches(html, "<tr[^>]*data-row-href=\"[^\"]+\"[^>]*>.*?</tr>", RegexOptions.Singleline);
        Assert.Equal(4, rows.Count);
        foreach (Match row in rows)
        {
            string destination = Href(row, "data-row-href"); Assert.Equal(destination, Href(Regex.Match(row.Value, "<a[^>]+href=\"[^\"]+\"[^>]*>"), "href"));
            using var target = await f.Get(destination.Split('#')[0]); Assert.Equal(HttpStatusCode.OK, target.StatusCode);
            string body = await target.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            if (destination.Contains("selectedDocumentId="))
            {
                Guid document = destination.Contains(RequestDocument.ToString()) ? RequestDocument : OrgDocument;
                Assert.EndsWith("#document-" + document.ToString("N"), destination);
                Assert.Contains("id=\"document-" + document.ToString("N") + "\" class=\"selected-document\"", body);
                Assert.Contains("Review Document", row.Value); Snapshot(document == OrgDocument ? "organization-document" : "request-document", body);
                Assert.Contains(document == OrgDocument ? "?id=" + Organization : "/Staff/Review/" + RequestId, destination);
            }
        }
        Assert.Equal(0, f.Data.Writes); Assert.Equal(0, f.Data.Downloads);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task DocumentDownloadsAndActionsRequireCorrectContextAndAntiforgery(bool request)
    {
        await using var f = await Setup();
        Guid document = request ? RequestDocument : OrgDocument, foreign = request ? OrgDocument : RequestDocument;
        string page = request ? "/Staff/Review/" + RequestId : "/Staff/Organization?id=" + Organization;
        string query = request ? "?" : "&";
        using var response = await f.Get(page); string token = Token(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)); Assert.NotEmpty(token);
        string download = page + query + "handler=" + (request ? "Download" : "DownloadDocument") + "&documentId=";
        using var valid = await f.Get(download + document); Assert.Equal(HttpStatusCode.OK, valid.StatusCode); Assert.Equal(1, f.Data.Downloads);
        using var denied = await f.Get(download + foreign); Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode); Assert.Null(denied.Headers.Location); Assert.Equal(1, f.Data.Downloads);
        foreach (string handler in new[] { "VerifyDocument", "RejectDocument", "ArchiveDocument" })
        {
            using var bad = await f.Post(page + query + "handler=" + handler, foreign, token); Assert.Equal(HttpStatusCode.NotFound, bad.StatusCode);
            using var csrf = await f.Post(page + query + "handler=" + handler, document, "invalid"); Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);
            using var good = await f.Post(page + query + "handler=" + handler, document, token); Assert.Equal(HttpStatusCode.Redirect, good.StatusCode);
        }
        Assert.Equal(3, f.Data.Writes);
        f.Data.Archived = true;
        using var archived = await f.Get(page + query + "selectedDocumentId=" + document); Assert.Equal(HttpStatusCode.Gone, archived.StatusCode);
        using var noDownload = await f.Get(download + document); Assert.Equal(HttpStatusCode.Gone, noDownload.StatusCode); Assert.Equal(1, f.Data.Downloads);
        using var noWrite = await f.Post(page + query + "handler=VerifyDocument", document, token); Assert.Equal(HttpStatusCode.Gone, noWrite.StatusCode); Assert.Equal(3, f.Data.Writes);
    }

    [Theory][InlineData("public")][InlineData("no-access")][InlineData("anonymous")][InlineData("expired")]
    public async Task UnauthorizedActorsCannotSearchReadOrMutateDocuments(string actor)
    {
        await using var f = await Setup(actor);
        foreach (string url in new[] { "/Staff?Search=John", "/Staff/Integrations", "/Staff/Integrations?handler=Export", "/Staff/Organizations", "/Staff/Organization?id=" + Organization, "/Staff/Review/" + RequestId + "?handler=Download&documentId=" + RequestDocument })
        {
            using var response = await f.Get(url); Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.Redirect });
            if (response.StatusCode == HttpStatusCode.Redirect) Assert.StartsWith("/Account/StaffLogin", response.Headers.Location!.OriginalString);
        }
        Assert.Equal(0, f.Data.Reads); Assert.Equal(0, f.Data.Writes); Assert.Equal(0, f.Data.Downloads);
    }
    [Theory][InlineData("Administrator")][InlineData("Reviewer")]
    public async Task IntegrationMonitoringRendersStatusDetailsHistoryExportsAndRestrictedActions(string actor)
    {
        await using var fixture=await Setup(actor);
        using var response=await fixture.Get("/Staff/Integrations?RequestId="+RequestId+"&Search=John&Status=Failed&Sort=oldest&From=2026-01-01&Through=2026-12-31");
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        string html=await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("JMDB01 / FFPJM",html);Assert.Contains("SQL_CONSTRAINT",html);Assert.Contains("Attempt history",html);
        Assert.Contains("Return-event outbox",html);Assert.Contains("REQ-FIXTURE",html);
        Assert.Equal(actor=="Administrator",html.Contains("Verify destination and retry"));
        Snapshot(actor=="Administrator"?"integrations":"integrations-reviewer",html);
        using var original=await fixture.Get("/Staff/Review/"+RequestId);
        Assert.Contains("FFP Manager transfer: Failed",await original.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var queue=await fixture.Get("/Staff");Assert.Contains("Integration failures / reconciliation",await queue.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("Search=John",html);Assert.Contains("Status=Failed",html);
        using var invalid=await fixture.Get("/Staff/Integrations?From=2026-12-31&Through=2026-01-01");Assert.Equal(HttpStatusCode.BadRequest,invalid.StatusCode);
        string token=Token(html);
        using var missing=await fixture.Get("/Staff/Integrations?RequestId="+Guid.NewGuid());Assert.Equal(HttpStatusCode.NotFound,missing.StatusCode);Assert.Null(missing.Headers.Location);
        fixture.Data.TransferApplicant="=SUM(1+1)";
        using var export=await fixture.Get("/Staff/Integrations?handler=Export&Search=John&Status=Failed");Assert.Equal(HttpStatusCode.OK,export.StatusCode);
        Assert.Equal("text/csv",export.Content.Headers.ContentType!.MediaType);Assert.Contains("'=SUM(1+1)",await export.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        foreach(string handler in new[]{"Retry","Reconcile"})
        {
            using var post=await fixture.Send(new(HttpMethod.Post,"/Staff/Integrations?handler="+handler){Content=new FormUrlEncodedContent(new Dictionary<string,string>{["RequestId"]=RequestId.ToString(),["linkId"]=Organization.ToString(),["__RequestVerificationToken"]=token})});
            Assert.Equal(actor=="Administrator"?HttpStatusCode.Redirect:HttpStatusCode.Forbidden,post.StatusCode);
        }
        Assert.Equal(actor=="Administrator"?2:0,fixture.Data.Writes);
    }

    [Fact]
    public async Task MissingContextsAndDocumentsStayNotFoundWithoutHomeRedirect()
    {
        await using var f = await Setup();
        foreach (string url in new[] { "/Staff/Review/" + Guid.NewGuid(), "/Staff/Organization?id=" + Guid.NewGuid(), "/Staff/Review/" + RequestId + "?selectedDocumentId=" + Guid.NewGuid(), "/Staff/Organization?id=" + Organization + "&selectedDocumentId=" + Guid.NewGuid() })
        { using var response = await f.Get(url); Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); Assert.Null(response.Headers.Location); }
    }
}
