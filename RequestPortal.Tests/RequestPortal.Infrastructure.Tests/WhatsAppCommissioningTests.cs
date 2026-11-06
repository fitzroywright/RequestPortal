using System.Net;
using System.Text;
using System.Text.Json;
using Common.Messaging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;
using RequestPortal.Web.Services.WhatsAppCommissioning;
using RequestPortal.Infrastructure.Messaging;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class WhatsAppCommissioningTests
{
    static NotificationOptions Settings => new() { WhatsAppWebhookEnabled = true,
        WhatsAppPhoneNumberId = CommissioningGateway.Phone, WhatsAppBusinessAccountId = CommissioningGateway.Account,
        WhatsAppAccessTokenSecretName = CommissioningGateway.TokenReference, WhatsAppGraphApiVersion = "v23.0" };
    const string Recipient = "+15555550123";
    static readonly Guid Actor = Guid.NewGuid();

    [Fact]
    public async Task ExistingChannelSendsOnlyHelloWorldAndCapturesIdWithoutEnablingGeneralDelivery()
    {
        using var http = new Meta();
        using var client = new HttpClient(http);
        var result = await new CommissioningGateway(client, new Secrets()).SendHelloWorldAsync(Settings, Recipient, Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.Equal("Accepted", result.State);
        Assert.Equal("wamid.fixture-outbound", result.MessageId);
        Assert.Equal(1, http.Posts);
        using var json = JsonDocument.Parse(http.LastBody!);
        Assert.Equal("template", json.RootElement.GetProperty("type").GetString());
        Assert.Equal("hello_world", json.RootElement.GetProperty("template").GetProperty("name").GetString());
        Assert.Equal("en_US", json.RootElement.GetProperty("template").GetProperty("language").GetProperty("code").GetString());
        Assert.False(json.RootElement.GetProperty("template").TryGetProperty("components",out _));
        Assert.Equal(Recipient[1..],json.RootElement.GetProperty("to").GetString());
        Assert.False(Settings.WhatsAppEnabled);
    }
    [Theory]
    [InlineData(400, "{\"error\":{\"code\":131030,\"error_subcode\":7,\"message\":\"PRIVATE recipient, token and body\"}}", "Rejected", 131030)]
    [InlineData(500, "{\"error\":{\"code\":2,\"message\":\"PRIVATE\"}}", "Unknown", 2)]
    [InlineData(200, "{\"messages\":[]}", "Unknown", null)]
    [InlineData(200, "PRIVATE malformed body", "Unknown", null)]
    public async Task MetaErrorsAndMalformedSuccessAreSafeAndNeverRetried(int status, string body, string state, int? code)
    {
        using var http = new Meta { PostStatus = status, PostBody = body };
        using var client = new HttpClient(http);
        var result = await new CommissioningGateway(client, new Secrets()).SendHelloWorldAsync(Settings, Recipient, Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.Equal(state, result.State); Assert.Equal(code, result.ErrorCode);
        Assert.Equal(1, http.Posts);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(result));
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task UnavailableSecretOrAmbiguousNetworkNeverLeaksExceptionOrRetries(bool missingSecret)
    {
        using var http = new Meta { NetworkError = !missingSecret };
        using var client = new HttpClient(http);
        var result = await new CommissioningGateway(client, new Secrets { Missing = missingSecret }).SendHelloWorldAsync(Settings, Recipient, Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.Equal(missingSecret ? "NotSent" : "Unknown", result.State);
        Assert.Equal(missingSecret ? 0 : 1, http.Posts);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(result));
    }
    [Theory]
    [InlineData("sender")][InlineData("permissions")][InlineData("template")]
    public async Task MissingSenderPermissionOrTemplateBlocksReadiness(string stage)
    {
        using var http = new Meta { BadStage = stage };
        using var client = new HttpClient(http);
        var result = await new CommissioningGateway(client, new Secrets()).InspectAsync(Settings, TestContext.Current.CancellationToken);
        Assert.False(result.Ready); Assert.Equal(stage, result.Stage); Assert.Equal(0, http.Posts);
    }
    [Theory]
    [InlineData("enabled")][InlineData("phone")][InlineData("account")][InlineData("token")][InlineData("version")][InlineData("webhook")]
    public void OtherSendersReferencesVersionsAndGeneralOutboundFailClosed(string fault)
    {
        var settings = Settings;
        switch (fault) { case "enabled": settings.WhatsAppEnabled=true; break; case "phone":settings.WhatsAppPhoneNumberId="other";break;
            case "account":settings.WhatsAppBusinessAccountId="other";break; case "token":settings.WhatsAppAccessTokenSecretName="other";break;
            case "version":settings.WhatsAppGraphApiVersion="v23.0/other";break; case "webhook":settings.WhatsAppWebhookEnabled=false;break; }
        Assert.Throws<InvalidOperationException>(() => CommissioningGateway.RequireTestConfiguration(settings));
    }
    [Theory]
    [InlineData(false, true, Recipient, Recipient)]
    [InlineData(true, false, Recipient, Recipient)]
    [InlineData(true, true, Recipient, "+15555550124")]
    [InlineData(true, true, "not-a-number", "not-a-number")]
    public async Task RecipientMustBeExplicitlyAuthorizedVerifiedAndRepeated(bool verified, bool authorized, string recipient, string repeated)
    {
        using var http = new Meta(); var journal = new Journal();
        var service = Service(journal, http);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AuthorizeAsync(Actor, recipient, repeated, verified, authorized, TestContext.Current.CancellationToken));
        Assert.Null(journal.Test); Assert.Equal(0,http.Posts);
    }
    [Fact]
    public async Task ConcurrentDuplicateSendCanReachMetaOnlyOnceAndRecipientIsEncrypted()
    {
        using var http = new Meta(); var journal = new Journal(); var service = Service(journal,http);
        var ct = TestContext.Current.CancellationToken;
        Guid id = await service.AuthorizeAsync(Actor, Recipient, Recipient, true, true, ct);
        Assert.DoesNotContain(Recipient, JsonSerializer.Serialize(journal.Test));
        Assert.Equal(0,http.Posts); // Authorization never sends.
        async Task<bool> Send() { try { await service.SendAsync(Actor,id,"SEND HELLO_WORLD",ct);return true; } catch(InvalidOperationException){return false;} }
        var results = await Task.WhenAll(Send(),Send());
        Assert.Single(results,x=>x); Assert.Equal(1,http.Posts);
        var status = await service.StatusAsync(Actor,id,ct);
        Assert.Equal("Accepted",status!.State); Assert.NotNull(status.MessageIdFingerprint);
        Assert.DoesNotContain("wamid.fixture-outbound",JsonSerializer.Serialize(status));
        Assert.DoesNotContain(Recipient,JsonSerializer.Serialize(status));
        Assert.Equal(1,journal.Claims);
    }
    [Fact]
    public async Task ExpiredAuthorizationWrongActorAndMissingSendConfirmationNeverSend()
    {
        using var http = new Meta(); var journal = new Journal(); var service = Service(journal,http); var ct=TestContext.Current.CancellationToken;
        Guid id=await service.AuthorizeAsync(Actor,Recipient,Recipient,true,true,ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.SendAsync(Guid.NewGuid(),id,"SEND HELLO_WORLD",ct));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.SendAsync(Actor,id,"",ct));
        journal.Test=journal.Test! with { ExpiresUtc=DateTimeOffset.UtcNow.AddMinutes(-1) };
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.SendAsync(Actor,id,"SEND HELLO_WORLD",ct));
        Assert.Equal(0,http.Posts);Assert.Equal(0,journal.Claims);
    }
    [Fact]
    public async Task LostCompletionRecordLeavesAttemptedWhichCannotBeRetried()
    {
        using var http = new Meta(); var journal = new Journal { FailCompletion=true }; var service=Service(journal,http); var ct=TestContext.Current.CancellationToken;
        Guid id=await service.AuthorizeAsync(Actor,Recipient,Recipient,true,true,ct);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.SendAsync(Actor,id,"SEND HELLO_WORLD",ct));
        Assert.Equal("Attempted",journal.Test!.State);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.SendAsync(Actor,id,"SEND HELLO_WORLD",ct));
        Assert.Equal(1,http.Posts);
    }
    static CommissioningService Service(Journal journal, Meta http) => new(journal, new(new HttpClient(http),new Secrets()), Options.Create(Settings),new EphemeralDataProtectionProvider(),new Authorization(),TimeProvider.System);
    sealed class Authorization : IIntegrationOperatorAuthorization
    {
        public Task DemandAsync(Guid actor,CancellationToken ct=default) => actor==Actor ? Task.CompletedTask : throw new UnauthorizedAccessException();
    }
    sealed class Secrets : IChannelSecretResolver
    {
        public bool Missing;
        public Task<string?> GetSecretAsync(string name,CancellationToken ct=default)
        { Assert.Equal(CommissioningGateway.TokenReference,name); return Task.FromResult<string?>(Missing?null:"PRIVATE_FIXTURE_TOKEN"); }
    }
    sealed class Meta : HttpMessageHandler
    {
        public int Posts; public string? LastBody; public int PostStatus=200; public bool NetworkError; public string? BadStage;
        public string PostBody="{\"messages\":[{\"id\":\"wamid.fixture-outbound\"}]}";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Assert.Equal("graph.facebook.com",request.RequestUri!.Host); Assert.Equal("https",request.RequestUri.Scheme);
            Assert.Equal("PRIVATE_FIXTURE_TOKEN",request.Headers.Authorization?.Parameter);
            if(request.Method==HttpMethod.Post)
            { Interlocked.Increment(ref Posts); LastBody=await request.Content!.ReadAsStringAsync(ct); if(NetworkError)throw new HttpRequestException("PRIVATE provider details"); return Response(PostStatus,PostBody); }
            if(request.RequestUri.AbsolutePath.EndsWith("/permissions")) return Response(200,BadStage=="permissions"?"{\"data\":[]}":"{\"data\":[{\"permission\":\"whatsapp_business_messaging\",\"status\":\"granted\"}]}");
            if(request.RequestUri.AbsolutePath.EndsWith("/message_templates")) return Response(200,BadStage=="template"?"{\"data\":[]}":"{\"data\":[{\"name\":\"hello_world\",\"status\":\"APPROVED\",\"language\":\"en_US\"}]}");
            return Response(200,$"{{\"id\":\"{(BadStage=="sender"?"other":CommissioningGateway.Phone)}\",\"code_verification_status\":\"VERIFIED\",\"platform_type\":\"CLOUD_API\"}}");
        }
        static HttpResponseMessage Response(int status,string json)=>new((HttpStatusCode)status){Content=new StringContent(json,Encoding.UTF8,"application/json")};
    }
    sealed class Journal : ICommissioningJournal
    {
        public CommissioningTest? Test; public int Claims; public bool FailCompletion;
        public Task CreateAsync(CommissioningTest test,CancellationToken ct){Test=test;return Task.CompletedTask;}
        public Task<CommissioningTest?> ReadAsync(Guid id,Guid actor,CancellationToken ct)=>Task.FromResult(Test?.Id==id&&Test.ActorId==actor?Test:null);
        public Task<CommissioningTest?> ClaimAsync(Guid id,Guid actor,DateTimeOffset now,CancellationToken ct)
        { lock(this){if(Test?.Id!=id||Test.ActorId!=actor||Test.State!="Authorized"||Test.ExpiresUtc<=now)return Task.FromResult<CommissioningTest?>(null);Claims++;Test=Test with {State="Attempted",AttemptedUtc=now};return Task.FromResult<CommissioningTest?>(Test);} }
        public Task CompleteAsync(CommissioningTest test,CancellationToken ct){if(FailCompletion)throw new InvalidOperationException("PRIVATE DB details");Test=test;return Task.CompletedTask;}
        public Task<IReadOnlyList<CommissioningDelivery>> DeliveryAsync(string id,CancellationToken ct)=>Task.FromResult<IReadOnlyList<CommissioningDelivery>>([]);
    }
}
