using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Diagnostics;
using Common.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RequestPortal.Infrastructure.Messaging;
using RequestPortal.Web.Services;
using Xunit;
using Proxy = RequestPortal.Infrastructure.Tests.MessageNavigationPageTests.Proxy;

namespace RequestPortal.Infrastructure.Tests;

public sealed class WhatsAppWebhookTests
{
    private const string Token = "test-only-verification-token";
    private const string Secret = "test-only-meta-app-secret";
    private static NotificationOptions Settings => new()
    {
        WhatsAppWebhookEnabled = true, WhatsAppPhoneNumberId = "1427917097069650",
        WhatsAppBusinessAccountId = "1823610105620506"
    };

    [Theory]
    [InlineData("hub.mode=subscribe&hub.verify_token=test-only-verification-token&hub.challenge=001234", 200)]
    [InlineData("hub.mode=subscribe&hub.verify_token=incorrect&hub.challenge=1234", 403)]
    [InlineData("hub.mode=unsubscribe&hub.verify_token=test-only-verification-token&hub.challenge=1234", 403)]
    [InlineData("hub.mode=subscribe&hub.challenge=1234", 403)]
    [InlineData("hub.mode=subscribe&hub.verify_token=test-only-verification-token", 403)]
    [InlineData("", 403)]
    [InlineData("hub.mode=subscribe&hub.mode=subscribe&hub.verify_token=test-only-verification-token&hub.challenge=1234", 403)]
    [InlineData("hub.mode=subscribe&hub.verify_token=test-only-verification-token&hub.challenge=%0A", 403)]
    public async Task GetVerification(string query, int expected)
    {
        await using var app = await Host(new Store());
        var response = await app.GetTestClient().GetAsync("/api/webhooks/whatsapp?" + query, TestContext.Current.CancellationToken);
        Assert.Equal(expected, (int)response.StatusCode);
        if (expected == 200)
        {
            Assert.Equal("001234", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
        }
    }

    [Fact]
    public async Task OversizedVerificationIsRejected()
    {
        await using var app = await Host(new Store());
        Assert.Equal(HttpStatusCode.Forbidden, (await app.GetTestClient().GetAsync(
            "/api/webhooks/whatsapp?hub.mode=subscribe&hub.verify_token=" + new string('x', 513) + "&hub.challenge=1", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData("missing")][InlineData("invalid")][InlineData("tampered")][InlineData("multiple")]
    public async Task InvalidSignaturesCannotReachStore(string variant)
    {
        var store = new Store();
        await using var app = await Host(store);
        var body = Payload();
        using var request = Request(body);
        request.Headers.Remove("X-Hub-Signature-256");
        if (variant != "missing") request.Headers.TryAddWithoutValidation("X-Hub-Signature-256",
            variant == "invalid" ? "sha256=" + new string('0', 64) : Sign(body + " "));
        if (variant == "multiple") request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", Sign(body));
        Assert.Equal(HttpStatusCode.Forbidden, (await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Empty(store.Events);
    }

    [Theory]
    [InlineData("{")][InlineData("[]")][InlineData("{}")]
    [InlineData("{\"object\":\"whatsapp_business_account\",\"entry\":{}}")]
    public async Task MalformedSignedPayloadIsRejected(string body)
    {
        var store = new Store();
        await using var app = await Host(store);
        using var request = Request(body);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Empty(store.Events);
    }

    [Theory]
    [InlineData("sent")][InlineData("delivered")][InlineData("read")][InlineData("failed")][InlineData("future-state")]
    public async Task StatusCallbacksRetainSafeMetadata(string status)
    {
        var store = new Store();
        await using var app = await Host(store);
        using var request = Request(Payload(status));
        Assert.Equal(HttpStatusCode.OK, (await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);
        var item = Assert.Single(store.Events);
        Assert.Equal(status == "future-state" ? "unknown" : status, item.State);
        Assert.Equal("status", item.Kind);
        if (status == "failed") Assert.Equal(131026, Assert.Single(item.ErrorCodes));
        Assert.DoesNotContain("SENSITIVE", JsonSerializer.Serialize(item));
    }

    [Fact]
    public async Task InboundIsAcceptedWithoutSensitiveContentsOrIdentityChanges()
    {
        var store = new Store();
        await using var app = await Host(store);
        using var request = Request(Payload());
        Assert.Equal(HttpStatusCode.OK, (await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);
        var item = Assert.Single(store.Events);
        Assert.Equal("inbound", item.Kind);
        Assert.Equal("text", item.State);
        Assert.DoesNotContain("SENSITIVE", JsonSerializer.Serialize(item));
    }

    [Fact]
    public async Task UnknownFieldIsDurablyAccountedFor()
    {
        var store = new Store();
        await using var app = await Host(store);
        using var request = Request(Payload().Replace("\"field\":\"messages\"", "\"field\":\"future_event\""));
        Assert.Equal(HttpStatusCode.OK, (await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal("ignored", Assert.Single(store.Events).Kind);
    }

    [Theory]
    [InlineData("1427917097069650", "wrong-account")]
    [InlineData("wrong-phone", "1823610105620506")]
    public async Task UnconfiguredIdentityIsRejected(string phone, string account)
    {
        var store = new Store();
        await using var app = await Host(store);
        using var request = Request(Payload().Replace("1427917097069650", phone).Replace("1823610105620506", account));
        Assert.Equal(HttpStatusCode.BadRequest, (await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Empty(store.Events);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task OversizedBodyIsRejectedWithAndWithoutContentLength(bool chunked)
    {
        var store = new Store();
        await using var app = await Host(store);
        string body = new('x', WhatsAppWebhookEndpoints.MaxBodyBytes + 1);
        using var request = Request(body);
        if (chunked) { request.Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body))); request.Headers.TransferEncodingChunked = true; }
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Empty(store.Events);
    }

    [Fact]
    public async Task StoreFailureIsNotAcknowledged()
    {
        await using var app = await Host(new Store { Fail = true });
        using var request = Request(Payload());
        var response = await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("SENSITIVE", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnonymousWebhookDoesNotMakeProtectedEndpointsAnonymous()
    {
        await using var app = await Host(new Store());
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetTestClient().GetAsync("/api/protected", TestContext.Current.CancellationToken)).StatusCode);
        using var request = Request(Payload());
        Assert.Equal(HttpStatusCode.OK, (await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task MissingSecretsFailClosedAndDisabledWebhookDoesNotNeedThem()
    {
        foreach (bool enabled in new[] { true, false })
        {
            var settings = Settings; settings.WhatsAppWebhookEnabled = enabled;
            await using var app = await Host(new Store(), settings, missingSecrets: true);
            using var request = Request(Payload());
            Assert.Equal(enabled ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NotFound,
                (await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);
            Assert.Equal(enabled ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NotFound,
                (await app.GetTestClient().GetAsync("/api/webhooks/whatsapp?hub.mode=subscribe&hub.verify_token=" + Token + "&hub.challenge=1", TestContext.Current.CancellationToken)).StatusCode);
        }
    }

    [Fact]
    public async Task PersistedJournalDeduplicatesAcrossContextsAndRetainsStatusHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(ct);
        var diagnostics = new List<DiagnosticTelemetryEvent>();
        var router = Proxy.For<IDiagnosticOperationsRouter>((_, args) => { diagnostics.Add((DiagnosticTelemetryEvent)args[0]!); return Task.CompletedTask; });
        var item = new WhatsAppWebhookEvent("account", "phone", "message", "inbound", "text", "123", []);
        var store = new WhatsAppWebhookStore(database.Db, router);
        await store.AcceptAsync([item, item], ct);
        await using var other = database.CreateSiblingContext();
        await new WhatsAppWebhookStore(other, router).AcceptAsync([item], ct);
        foreach (var state in new[] { "sent", "delivered", "read", "failed" })
            await store.AcceptAsync([item with { Kind = "status", State = state, ErrorCodes = state == "failed" ? [131026] : [] }], ct);
        Assert.Equal(5, await database.Db.InboxMessages.CountAsync(x => x.Consumer == WhatsAppWebhookStore.Consumer, ct));
        var rows = await database.Db.OutboxMessages.Where(x => x.MessageType == WhatsAppWebhookStore.Consumer).ToListAsync(ct);
        Assert.Equal(5, rows.Count);
        Assert.All(rows, row => Assert.NotNull(row.ProcessedUtc));
        Assert.Single(diagnostics);
    }

    [Fact]
    public async Task JournalFailureRollsBackInboxAndAllowsRetry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(ct);
        var router = Proxy.For<IDiagnosticOperationsRouter>((_, _) => Task.CompletedTask);
        var store = new WhatsAppWebhookStore(database.Db, router);
        var item = new WhatsAppWebhookEvent("account", "phone", "message", "status", "sent", "123", []);
        await database.Db.Database.ExecuteSqlRawAsync("ALTER TABLE outbox_messages ADD CONSTRAINT test_reject_receipt CHECK (\"MessageType\" <> 'RequestPortal.WhatsAppWebhook.v1')", ct);
        await Assert.ThrowsAnyAsync<Exception>(() => store.AcceptAsync([item], ct));
        Assert.False(await database.Db.InboxMessages.AnyAsync(x => x.Consumer == WhatsAppWebhookStore.Consumer, ct));
        await database.Db.Database.ExecuteSqlRawAsync("ALTER TABLE outbox_messages DROP CONSTRAINT test_reject_receipt", ct);
        await store.AcceptAsync([item], ct);
        Assert.True(await database.Db.InboxMessages.AnyAsync(x => x.Consumer == WhatsAppWebhookStore.Consumer, ct));
    }

    private static async Task<WebApplication> Host(IWhatsAppWebhookStore store, NotificationOptions? settings = null, bool missingSecrets = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(Options.Create(settings ?? Settings));
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(Proxy.For<ISecretProvider>((method, args) => method == "GetAsync"
            ? Task.FromResult<string?>(missingSecrets ? null : (string)args[0]! == Settings.WhatsAppAppSecretName ? Secret : Token)
            : throw new InvalidOperationException(method)));
        builder.Services.AddAuthentication("test-cookie").AddCookie("test-cookie", options =>
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; });
        builder.Services.AddAuthorization(options => options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization();
        app.MapWhatsAppWebhookEndpoints();
        app.MapGet("/api/protected", () => "protected").RequireAuthorization();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static string Sign(string body) => "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    private static HttpRequestMessage Request(string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/whatsapp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Hub-Signature-256", Sign(body)); return request;
    }
    private static string Payload(string? status = null) => JsonSerializer.Serialize(new
    {
        @object = "whatsapp_business_account",
        entry = new[] { new { id = "1823610105620506", changes = new[] { new { field = "messages", value = new
        {
            messaging_product = "whatsapp", metadata = new { phone_number_id = "1427917097069650" },
            messages = status == null ? new[] { new { id = "wamid.test", timestamp = "123", type = "text", from = "SENSITIVE", text = new { body = "SENSITIVE" } } } : [],
            statuses = status != null ? new[] { new { id = "wamid.test", timestamp = "124", status, recipient_id = "SENSITIVE", errors = status == "failed" ? new[] { new { code = 131026, title = "SENSITIVE" } } : [] } } : []
        } } } } }
    });
    private sealed class Store : IWhatsAppWebhookStore
    {
        public bool Fail { get; init; }
        public List<WhatsAppWebhookEvent> Events { get; } = [];
        public Task AcceptAsync(IReadOnlyList<WhatsAppWebhookEvent> events, CancellationToken cancellationToken)
        {
            if (Fail) throw new InvalidOperationException("SENSITIVE");
            Events.AddRange(events); return Task.CompletedTask;
        }
    }
}
