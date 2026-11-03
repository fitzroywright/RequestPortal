using System.Net;
using System.Security.Claims;
using Common.Secrets;
using Common.Security.Integration;
using Common.Security.Models;
using Microsoft.Extensions.Options;
using RequestPortal.Web.Security;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class StaffDirectoryLookupTests
{
    private const string Tenant="99d3b4fe-13c0-4901-a1a5-a382c4a99432";
    private const string ObjectId="5c6e1d4e-a815-4c8e-bcd4-c7bf6902f6f7";
    private sealed class Secrets : SecretProviderBase
    {
        public string? Name;
        public override Task<string?> GetAsync(string name,CancellationToken cancellationToken=default) { Name=name; return Task.FromResult<string?>("directory-client-secret"); }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public readonly List<(string Method,string Url,string? Bearer,string? Body)> Requests=[];
        public readonly Queue<(HttpStatusCode Status,string Json)> Responses=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            Requests.Add((request.Method.Method,request.RequestUri!.ToString(),request.Headers.Authorization?.Parameter,
                request.Content is null?null:await request.Content.ReadAsStringAsync(cancellationToken)));
            var response=Responses.Dequeue();
            return new(response.Status){Content=new StringContent(response.Json)};
        }
    }
    private static (HttpStaffDirectoryLookup Service,StaffDirectoryOptions Options,StaffOidcOptions Staff,Handler Handler,Secrets Secrets) Setup()
    {
        var options=new StaffDirectoryOptions{Enabled=true,Provider="Entra",ClientId="directory-reader",TenantId=Tenant,ClientSecretName="approved/directory-client"};
        var staff=new StaffOidcOptions{Enabled=true,ProviderId="staff",Authority=$"https://login.microsoftonline.com/{Tenant}/v2.0",AllowedTenantIds=[Tenant]};
        var handler=new Handler(); var secrets=new Secrets();
        var service=new HttpStaffDirectoryLookup(new HttpClient(handler),Microsoft.Extensions.Options.Options.Create(options),Microsoft.Extensions.Options.Options.Create(staff),secrets);
        return (service,options,staff,handler,secrets);
    }
    private static void Respond(Handler handler,string json,HttpStatusCode status=HttpStatusCode.OK)
    {
        handler.Responses.Enqueue((HttpStatusCode.OK,"{\"access_token\":\"directory-token\"}"));
        handler.Responses.Enqueue((status,json));
    }

    [Fact]
    public async Task VerifiedDirectoryIdentityMatchesActualOidcIdentityKey()
    {
        var f=Setup();
        Respond(f.Handler,$"{{\"id\":\"{ObjectId}\",\"userPrincipalName\":\"user@example.org\",\"displayName\":\"Directory User\",\"mail\":\"user@example.org\",\"accountEnabled\":true}}");
        var user=await f.Service.FindAsync(ObjectId,TestContext.Current.CancellationToken);
        Assert.NotNull(user); Assert.True(user.Enabled); Assert.Equal("Directory User",user.DisplayName);
        var claims=new List<Claim>{new("sub","pairwise-sub-is-not-the-object-id"),new("preferred_username","user@example.org")};
        claims.Add(new("oid",ObjectId)); claims.Add(new("tid",Tenant));
        var proof=VerifiedExternalIdentityFactory.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(claims,"validated-oidc")),
            new("staff",f.Staff.Authority,AuthenticationProviderKind.EntraId,f.Staff.AllowedTenantIds));
        Assert.Equal(proof.Key,user.Key); Assert.Equal(proof.ProviderKind,user.Kind);
        Assert.Equal("approved/directory-client",f.Secrets.Name);
        Assert.Equal("POST",f.Handler.Requests[0].Method);
        Assert.Contains("grant_type=client_credentials",f.Handler.Requests[0].Body);
        Assert.Equal("GET",f.Handler.Requests[1].Method); Assert.Equal("directory-token",f.Handler.Requests[1].Bearer);
        Assert.DoesNotContain("directory-client-secret",f.Handler.Requests[1].Url);
        Assert.StartsWith("https://graph.microsoft.com/v1.0/users/",f.Handler.Requests[1].Url);
        Assert.Equal($"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token",f.Handler.Requests[0].Url);
        Assert.Contains("scope=https%3A%2F%2Fgraph.microsoft.com%2F.default",f.Handler.Requests[0].Body);
        Assert.Contains("$select=id,displayName,userPrincipalName,mail,accountEnabled",f.Handler.Requests[1].Url);
    }

    [Fact]
    public async Task SearchesAreBoundedAndEscapedAndDisabledUsersRemainDisabled()
    {
        var f=Setup();
        string row=$"{{\"id\":\"{ObjectId}\",\"userPrincipalName\":\"user@example.org\",\"displayName\":\"User\",\"accountEnabled\":false}}";
        Respond(f.Handler,"{\"value\":["+row+"]}");
        var users=await f.Service.SearchAsync("O'Neil & staff",TestContext.Current.CancellationToken);
        Assert.False(Assert.Single(users).Enabled);
        string url=Uri.UnescapeDataString(f.Handler.Requests[1].Url);
        Assert.Contains("O''Neil & staff",url); Assert.Contains("$top=21",url);
        Assert.Equal(2,f.Handler.Requests.Count);
    }

    [Theory]
    [InlineData("disabled")] [InlineData("http")] [InlineData("unsupported-provider")] [InlineData("mixed-provider")] [InlineData("different-issuer")] [InlineData("missing-client")]
    public async Task UnsafeOrIncompleteConfigurationMakesNoRemoteRequests(string mode)
    {
        var f=Setup();
        switch(mode){
            case "disabled":f.Options.Enabled=false;break;
            case "http":f.Staff.Authority=$"http://login.microsoftonline.com/{Tenant}/v2.0";break;
            case "unsupported-provider":f.Options.Provider="unsupported";break;
            case "mixed-provider":f.Staff.AllowedTenantIds=[];break;
            case "different-issuer":f.Staff.Issuer="https://other.example/v2.0";break;
            case "missing-client":f.Options.ClientId="";break;
        }
        Assert.NotNull(f.Service.UnavailableReason);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.SearchAsync("user",TestContext.Current.CancellationToken));
        Assert.Empty(f.Handler.Requests); Assert.Null(f.Secrets.Name);
    }

    [Fact]
    public async Task EntraMustMatchAllowedTenantAndAuthority()
    {
        var f=Setup(); f.Staff.AllowedTenantIds=[Guid.NewGuid().ToString("D")]; Assert.NotNull(f.Service.UnavailableReason);
        f.Staff.AllowedTenantIds=[Tenant]; f.Staff.Authority="https://login.microsoftonline.com/common/v2.0"; Assert.NotNull(f.Service.UnavailableReason);
    }

    [Theory] [InlineData("ab")] [InlineData("abc\nquery")]
    public async Task InvalidSearchMakesNoRemoteRequests(string query)
    {
        var f=Setup();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.SearchAsync(query,TestContext.Current.CancellationToken));
        Assert.Empty(f.Handler.Requests);
    }

    [Fact]
    public async Task MissingUserReturnsNullAndPermissionErrorsDoNotLeakProviderResponses()
    {
        var f=Setup(); Respond(f.Handler,"{}",HttpStatusCode.NotFound);
        Assert.Null(await f.Service.FindAsync(ObjectId,TestContext.Current.CancellationToken));
        Respond(f.Handler,"sensitive provider response",HttpStatusCode.Forbidden);
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.FindAsync(ObjectId,TestContext.Current.CancellationToken));
        Assert.Contains("HTTP 403",error.Message); Assert.DoesNotContain("sensitive",error.Message);
    }

    [Theory] [InlineData("..")][InlineData("user/other")][InlineData("user\\other")][InlineData("not-a-guid")][InlineData("user@example.org")]
    public async Task InvalidIdsCannotChangeProviderEndpoint(string id)
    {
        var f=Setup();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.FindAsync(id,TestContext.Current.CancellationToken));
        Assert.Empty(f.Handler.Requests);
    }
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RejectedClientCredentialNeverCallsGraph(HttpStatusCode status)
    {
        var f=Setup(); f.Handler.Responses.Enqueue((status,"sensitive token response"));
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.FindAsync(ObjectId,TestContext.Current.CancellationToken));
        Assert.Single(f.Handler.Requests); Assert.DoesNotContain("sensitive",error.Message);
    }

    [Theory]
    [InlineData("null")][InlineData("[]")][InlineData("{}")][InlineData("{\"access_token\":123}")][InlineData("{\"access_token\":\"\"}")]
    public async Task MalformedTokenResponseNeverCallsGraph(string json)
    {
        var f=Setup(); f.Handler.Responses.Enqueue((HttpStatusCode.OK,json));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.FindAsync(ObjectId,TestContext.Current.CancellationToken));
        Assert.Single(f.Handler.Requests);
    }

    [Theory]
    [InlineData("null")][InlineData("{}")] [InlineData("{\"value\":null}")][InlineData("{\"value\":[null]}")]
    public async Task MalformedSearchResponseIsRejected(string json)
    {
        var f=Setup(); Respond(f.Handler,json);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.SearchAsync("user",TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingAccountEnabledFailsClosedAndObjectIdIsCanonical()
    {
        var f=Setup();
        Respond(f.Handler,$"{{\"id\":\"{ObjectId.ToUpperInvariant()}\",\"userPrincipalName\":\"user@example.org\"}}");
        var user=await f.Service.FindAsync(ObjectId,TestContext.Current.CancellationToken);
        Assert.NotNull(user); Assert.False(user.Enabled); Assert.Equal(ObjectId,user.Key.SubjectId);
        Assert.Equal(Tenant,user.Key.ScopeId); Assert.Equal(AuthenticationProviderKind.EntraId,user.Kind);
    }

}
