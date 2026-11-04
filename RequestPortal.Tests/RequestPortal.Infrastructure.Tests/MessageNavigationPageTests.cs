using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
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
using RequestPortal.Application.Documents;
using RequestPortal.Application.Messages;
using RequestPortal.Domain.Messages;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Pages.Messages;
using RequestPortal.Web.Security;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class MessageNavigationPageTests
{
    private static readonly Guid Staff = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Owner = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Foreign = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid Linked = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly Guid Standalone = Guid.Parse("55555555-5555-4555-8555-555555555555");
    private static readonly Guid RequestId = Guid.Parse("66666666-6666-4666-8666-666666666666");
    private static readonly Guid Document = Guid.Parse("77777777-7777-4777-8777-777777777777");
    private static readonly string[] Folders = ["Inbox", "Assigned", "Unassigned", "Sent", "Archived"];
    private sealed class State
    {
        public Guid? LastThread;
        public int Reads, Downloads, Writes;
        public string? Reply;
        public Guid[] ReplyAttachments = [];
        public Guid? Assigned;
        public bool Closed;
        public readonly List<string> Actions = [];
        public bool Known(Guid id) => id == Linked || id == Standalone;
        public MessageThreadView Thread(Guid id) => new(id, id == Linked ? "Linked selected subject" : "Standalone selected subject", Owner, "Fixture organization",
            id == Linked ? RequestId : null, id == Linked ? "RP-MESSAGE-FIXTURE" : null, id == Linked ? MessageAudience.Request : MessageAudience.Individual,
            Assigned, Assigned.HasValue ? "Fixture staff" : null, null, Closed ? DateTimeOffset.UtcNow : null,
            [new(Guid.NewGuid(), Owner, "Fixture sender", "Representative", null, "Full original message body", DateTimeOffset.UtcNow.AddDays(-2), false,
                [new(Document, "evidence.pdf", "application/pdf", 32, true)]),
             new(Guid.NewGuid(), Staff, "Fixture staff", "FFP Staff", null, Reply ?? "Full second message body", DateTimeOffset.UtcNow.AddDays(-1), true, [])]);
        public IReadOnlyList<MailboxMessageView> Rows => new[] { Linked, Standalone }.Select(id => new MailboxMessageView(Guid.NewGuid(), Owner, id,
            id == Linked ? "Linked selected subject" : "Standalone selected subject", "Fixture sender", "Representative", null, "Short preview only", DateTimeOffset.UtcNow,
            null, Owner, "Fixture organization", id == Linked ? RequestId : null, id == Linked ? "RP-MESSAGE-FIXTURE" : null, true)).ToArray();
    }
    public class Proxy : DispatchProxy
    {
        public Func<string, object?[], object?> Call = (_, _) => throw new NotSupportedException();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!.Name, args ?? []);
        public static T For<T>(Func<string, object?[], object?> call) where T : class { var value = Create<T, Proxy>(); ((Proxy)(object)value).Call = call; return value; }
    }
    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string actor = Request.Headers["X-Test-Actor"].ToString();
            if (actor is "anonymous" or "expired") return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, (actor is "staff" or "no-access" ? Staff : actor == "foreign" ? Foreign : Owner).ToString()), new(ClaimTypes.Name, "Fixture user") };
            if (actor is "staff" or "no-access") claims.Add(new(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue));
            if (actor == "staff") claims.Add(new(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.Access));
            return Task.FromResult(AuthenticateResult.Success(new(new(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.Redirect((Scheme.Name == StaffOidcOptions.StaffCookieScheme ? "/Account/StaffLogin" : "/Account/Login") + "?ReturnUrl=" + Uri.EscapeDataString(Request.Path + Request.QueryString));
            return Task.CompletedTask;
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
            if (cookies.Count > 0) request.Headers.Add("Cookie", string.Join("; ", cookies.Select(pair => pair.Key + "=" + pair.Value)));
            var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var values)) foreach (var value in values) { var pair = value.Split(';', 2)[0].Split('=', 2); cookies[pair[0]] = pair[1]; }
            return response;
        }
        public Task<HttpResponseMessage> Get(string url) => Send(new(HttpMethod.Get, url));
        public Task<HttpResponseMessage> Post(string url, string token, string? reply = null, bool attachment = false)
        {
            var fields = new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["Reply"] = reply ?? "" };
            if (attachment) fields["AttachmentDocumentIds"] = Document.ToString();
            return Send(new(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(fields) });
        }
    }
    private static async Task<Fixture> Setup(string actor = "staff")
    {
        var data = new State();
        string root = Path.GetFullPath("../../../../../RequestPortal.Web", AppContext.BaseDirectory);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = typeof(ThreadModel).Assembly.GetName().Name,
            ContentRootPath = root, WebRootPath = "wwwroot", EnvironmentName = "Test" });
        builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
        builder.Services.AddRateLimiter(_ => { });
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddIdentityCore<ApplicationUser>();
        builder.Services.AddSingleton(Proxy.For<IUserStore<ApplicationUser>>((method, args) => method == "Dispose" ? null :
            Task.FromResult<ApplicationUser?>(new() { Id = Guid.Parse((string)args[0]!), DisplayName = "Fixture user" })));
        builder.Services.AddSingleton(Proxy.For<IPortalMessageService>((method, args) =>
        {
            if (method == "GetUnreadCountAsync") return Task.FromResult(2);
            if (new[] { "GetInboxAsync", "GetSentAsync", "GetArchivedAsync", "GetAssignedToMeAsync", "GetUnassignedAsync" }.Contains(method)) return Task.FromResult(data.Rows);
            Guid user = (Guid)args[0]!;
            if (method == "GetThreadAsync")
            {
                Guid id = (Guid)args[2]!; data.LastThread = id; data.Reads++;
                return Task.FromResult(data.Known(id) && user != Foreign ? data.Thread(id) : null);
            }
            if (method == "CanAccessAttachmentAsync") return Task.FromResult(user != Foreign && data.Known((Guid)args[2]!) && (Guid)args[3]! == Document);
            Guid threadId = (Guid)args[method == "ReplyAsync" ? 2 : 1]!;
            if (!data.Known(threadId)) throw new MessageThreadNotFoundException();
            if (user == Foreign) throw new UnauthorizedAccessException("Not a conversation participant.");
            data.Writes++; data.Actions.Add(method);
            switch (method)
            {
                case "AssignThreadAsync": data.Assigned = user; return Task.CompletedTask;
                case "UnassignThreadAsync": data.Assigned = null; return Task.CompletedTask;
                case "CloseThreadAsync": data.Closed = true; return Task.CompletedTask;
                case "ReopenThreadAsync": data.Closed = false; return Task.CompletedTask;
                case "ReplyAsync": data.Reply = (string)args[3]!; data.ReplyAttachments = ((IReadOnlyCollection<Guid>?)args[5])?.ToArray() ?? []; return Task.FromResult(Guid.NewGuid());
                default: throw new NotSupportedException(method);
            }
        }));
        builder.Services.AddSingleton(Proxy.For<IDocumentService>((method, args) => method switch
        {
            "GetRequestDocumentsAsync" => Task.FromResult<IReadOnlyList<DocumentSummary>>([new(Document, default, "evidence.pdf", "application/pdf", 32, DateTimeOffset.UtcNow, default, default, null, false, null)]),
            "DownloadAsync" => Download(data, (Guid)args[1]!), _ => throw new NotSupportedException(method)
        }));
        builder.Services.AddSingleton(Proxy.For<IDocumentPreviewRenderer>((_, _) => Task.FromResult<string?>(null)));
        builder.Services.AddAuthentication("fixture")
            .AddScheme<AuthenticationSchemeOptions, Authentication>("fixture", _ => { })
            .AddScheme<AuthenticationSchemeOptions, Authentication>(StaffOidcOptions.StaffCookieScheme, _ => { })
            .AddScheme<AuthenticationSchemeOptions, Authentication>(IdentityConstants.ApplicationScheme, _ => { })
            .AddScheme<AuthenticationSchemeOptions, Authentication>("ExternalReviewer", _ => { })
            .AddPolicyScheme(PortalMessageAuthentication.Scheme, null, options => options.ForwardDefaultSelector = PortalMessageAuthentication.SelectScheme);
        // Exercise the actual production conventions, policies, exception handler and 404 middleware.
        var assembly = typeof(ThreadModel).Assembly;
        assembly.GetType("RequestPortal.Web.Startup.AuthorizationServiceRegistration")!.GetMethod("Add")!.Invoke(null, [builder]);
        var app = builder.Build();
        assembly.GetType("RequestPortal.Web.Startup.ApplicationPipeline")!.GetMethod("Configure")!.Invoke(null, [app]);
        app.MapRazorPages();
        app.MapGet("/fixture/antiforgery", (Microsoft.AspNetCore.Http.HttpContext context, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Microsoft.AspNetCore.Http.Results.Content($"<input name=\"__RequestVerificationToken\" value=\"{tokens.RequestToken}\">", "text/html");
        }).RequireAuthorization();
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestServer().CreateClient(); client.BaseAddress = new("https://localhost"); client.DefaultRequestHeaders.Add("X-Test-Actor", actor);
        return new() { App = app, Client = client, Data = data };
    }
    private static Task<DocumentDownload?> Download(State state, Guid id)
    {
        state.Downloads++; return Task.FromResult<DocumentDownload?>(id == Document ? new(new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.4 fixture attachment")), "application/pdf", "evidence.pdf") : null);
    }
    private static string Token(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\""); Assert.True(match.Success); return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
    private static string Url(Guid id, string folder = "Inbox") => $"/Messages/Thread/{id}?returnTo=" + Uri.EscapeDataString("/Messages/" + folder);
    private static void Snapshot(string name, string html)
    {
        string? output = Environment.GetEnvironmentVariable("REQUESTPORTAL_MESSAGES_RENDER_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) return; Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, name + ".html"), html);
    }
    [Theory]
    [InlineData("Inbox")][InlineData("Assigned")][InlineData("Unassigned")][InlineData("Sent")][InlineData("Archived")]
    public async Task EveryFolderOpensBothExactConversationsAndKeepsContext(string folder)
    {
        await using var fixture = await Setup();
        fixture.Data.Closed = folder == "Archived";
        fixture.Data.Assigned = folder == "Assigned" ? Staff : null;
        using var response = await fixture.Get("/Messages/" + folder); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken); Snapshot(folder, html);
        var rows = Regex.Matches(html, "<a[^>]*class=\"outlook-message-grid outlook-message-row[^\"]*\"[^>]*>", RegexOptions.Singleline);
        Assert.Equal(2, rows.Count);
        foreach (Match row in rows)
        {
            string href = WebUtility.HtmlDecode(Regex.Match(row.Value, "href=\"([^\"]*)\"").Groups[1].Value);
            Guid id = href.Contains(Linked.ToString(), StringComparison.Ordinal) ? Linked : Standalone;
            Assert.StartsWith("/Messages/Thread/" + id, href); Assert.DoesNotContain("/Requests/Details", href);
            using var thread = await fixture.Get(href); Assert.Equal(HttpStatusCode.OK, thread.StatusCode); Assert.Equal(id, fixture.Data.LastThread);
            string content = await thread.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains(id == Linked ? "Linked selected subject" : "Standalone selected subject", content);
            Assert.Contains("Full original message body", content); Assert.Contains("Full second message body", content); Assert.DoesNotContain("Short preview only", content);
            Assert.Contains("href=\"/Messages/" + folder + "\">Back to mailbox", content);
            Assert.Contains("evidence.pdf", content); Assert.Contains("handler=" + (folder == "Archived" ? "Reopen" : folder == "Assigned" ? "Unassign" : "Assign"), content);
            if (folder != "Archived") Assert.Contains("handler=Close", content);
            if (id == Linked) { Assert.Contains("RP-MESSAGE-FIXTURE", content); Assert.Contains("href=\"/Staff/Review/" + RequestId, content); }
            else Assert.DoesNotContain(">View Request</a>", content);
            Snapshot(id == Linked ? "staff-linked" : "staff-standalone", content);
            Snapshot(folder + (id == Linked ? "-linked" : "-standalone"), content);
        }
        if (folder == "Inbox") Assert.Contains("outlook-unread", html);
        if (folder == "Archived") Assert.Contains("outlook-archived-row", html);
    }
    [Theory]
    [InlineData("Inbox")][InlineData("Sent")][InlineData("Archived")]
    public async Task ApplicantRowsUseThreadsAndSeparateAuthorizedRequestDetails(string folder)
    {
        await using var fixture = await Setup("owner");
        using var response = await fixture.Get("/Messages/" + folder); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string inbox = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken); Assert.DoesNotContain("href=\"/Requests/Details", inbox);
        using var thread = await fixture.Get(Url(Linked, folder)); Assert.Equal(HttpStatusCode.OK, thread.StatusCode);
        string html = await thread.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("href=\"/Requests/Details/" + RequestId, html); Assert.DoesNotContain("href=\"/Staff/Review/" + RequestId, html);
        Snapshot("applicant-linked", html);
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task AssignmentReplyAttachmentsClosingAndReturnNavigationRemainOnExactThread(bool linked)
    {
        await using var fixture = await Setup(); Guid id = linked ? Linked : Standalone; string url = Url(id, "Assigned");
        using var initial = await fixture.Get(url); string html = await initial.Content.ReadAsStringAsync(TestContext.Current.CancellationToken); string token = Token(html);
        foreach (string action in new[] { "Assign", "Unassign", "Reply", "Close", "Reopen" })
        {
            using var post = await fixture.Post(url + (action == "Reply" ? "" : "&handler=" + action), token, "Regression reply body", attachment: linked && action == "Reply");
            Assert.Equal(HttpStatusCode.Redirect, post.StatusCode); string location = post.Headers.Location!.OriginalString;
            Assert.StartsWith("/Messages/Thread/" + id, location); Assert.Contains("returnTo=", location);
            using var get = await fixture.Get(location); Assert.Equal(HttpStatusCode.OK, get.StatusCode); html = await get.Content.ReadAsStringAsync(TestContext.Current.CancellationToken); token = Token(html);
            Assert.Contains("href=\"/Messages/Assigned\">Back to mailbox", html);
            if (action == "Assign") { Assert.Equal(Staff, fixture.Data.Assigned); Assert.Contains("handler=Unassign", html); }
            if (action == "Unassign") Assert.Null(fixture.Data.Assigned);
            if (action == "Reply") { Assert.Contains("Regression reply body", html); Assert.Equal(linked ? new[] { Document } : [], fixture.Data.ReplyAttachments); }
            if (action == "Close") { Assert.Contains("This conversation is closed.", html); Assert.DoesNotContain("class=\"conversation-composer\"", html); }
        }
        Assert.Equal(5, fixture.Data.Writes);
        foreach (string attachmentUrl in new[] { $"/Messages/Thread/{id}?handler=Attachment&documentId={Document}", $"/Documents/Preview?handler=Raw&threadId={id}&documentId={Document}", $"/Documents/Preview?handler=Download&threadId={id}&documentId={Document}" })
        {
            using var attachment = await fixture.Get(attachmentUrl); Assert.Equal(HttpStatusCode.OK, attachment.StatusCode); Assert.Equal("application/pdf", attachment.Content.Headers.ContentType!.MediaType);
            Assert.StartsWith("%PDF", await attachment.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        Assert.Equal(3, fixture.Data.Downloads);
    }
    [Theory]
    [InlineData("foreign")][InlineData("missing")]
    public async Task UnavailableConversationsAndAttachmentsReturn404WithoutHomeRedirect(string actor)
    {
        await using var fixture = await Setup(actor == "missing" ? "staff" : actor); Guid id = actor == "missing" ? Guid.Empty : Linked;
        using var response = await fixture.Get(Url(id)); Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); Assert.Null(response.Headers.Location);
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken); Assert.Contains("Conversation not found", html); Assert.DoesNotContain("Full original message body", html);
        foreach (string url in new[] { $"/Messages/Thread/{id}?handler=Attachment&documentId={Document}", $"/Documents/Preview?threadId={id}&documentId={Document}" })
        { using var attachment = await fixture.Get(url); Assert.Equal(HttpStatusCode.NotFound, attachment.StatusCode); Assert.Null(attachment.Headers.Location); }
        Assert.Equal(0, fixture.Data.Downloads);
    }
    [Theory]
    [InlineData("anonymous")][InlineData("expired")]
    public async Task MissingOrExpiredSessionsFollowAuthenticationInsteadOfHome(string actor)
    {
        await using var fixture = await Setup(actor);
        foreach (string url in new[] { Url(Linked), $"/Documents/Preview?threadId={Linked}&documentId={Document}" })
        { using var response = await fixture.Get(url); Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); Assert.StartsWith("/Account/Login?ReturnUrl=", response.Headers.Location!.OriginalString); }
        Assert.Equal(0, fixture.Data.Reads); Assert.Equal(0, fixture.Data.Downloads);
    }
    [Fact]
    public async Task StaffWithoutPortalAccessIsDeniedBeforeConversationLoading()
    {
        await using var fixture = await Setup("no-access");
        foreach (string url in new[] { Url(Linked), $"/Documents/Preview?threadId={Linked}&documentId={Document}" })
        { using var response = await fixture.Get(url); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.Null(response.Headers.Location); }
        Assert.Equal(0, fixture.Data.Reads); Assert.Equal(0, fixture.Data.Downloads);
    }
    [Fact]
    public async Task ForeignReplyIsForbiddenAndMissingMutationsReturn404()
    {
        foreach (string actor in new[] { "foreign", "staff" })
        {
            await using var fixture = await Setup(actor);
            // Fixture-only endpoint issues a normal antiforgery token bound to this exact authenticated actor.
            using var tokenPage = await fixture.Get("/fixture/antiforgery");
            string token = Token(await tokenPage.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            foreach (string action in actor == "foreign" ? new[] { "Reply" } : new[] { "Reply", "Assign", "Unassign", "Close", "Reopen" })
            {
                string url = Url(actor == "foreign" ? Linked : Guid.Empty) + (action == "Reply" ? "" : "&handler=" + action);
                using var response = await fixture.Post(url, token, "Forbidden write");
                Assert.Equal(actor == "foreign" ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound, response.StatusCode); Assert.Null(response.Headers.Location);
            }
            Assert.Equal(0, fixture.Data.Writes);
        }
    }
    [Theory]
    [InlineData("https://outside.example.test")][InlineData("//outside.example.test")][InlineData("/Messages/../Account/Login")]
    public async Task ReturnTargetIsLimitedToMailboxPages(string target)
    {
        await using var fixture = await Setup(); using var response = await fixture.Get($"/Messages/Thread/{Linked}?returnTo=" + Uri.EscapeDataString(target));
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Contains("href=\"/Messages/Inbox\">Back to mailbox", html); Assert.DoesNotContain("href=\"" + target, html);
    }
    [Fact]
    public async Task InvalidMessageRoutesKeep404WhileUnrelatedNavigationRetainsExistingFallback()
    {
        await using var fixture = await Setup();
        using var message = await fixture.Get("/Messages/Thread/not-a-guid"); Assert.Equal(HttpStatusCode.NotFound, message.StatusCode); Assert.Null(message.Headers.Location);
        using var other = await fixture.Get("/a-page-that-does-not-exist"); Assert.Equal(HttpStatusCode.Redirect, other.StatusCode); Assert.Equal("/?navigationError=not-found", other.Headers.Location!.OriginalString);
    }
}
