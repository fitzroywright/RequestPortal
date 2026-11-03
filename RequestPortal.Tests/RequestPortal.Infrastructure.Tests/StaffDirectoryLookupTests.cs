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
    private static (HttpStaffDirectoryLookup Service,StaffDirectoryOptions Options,StaffOidcOptions Staff,Handler Handler,Secrets Secrets) Setup(bool entra=false)
    {
        var options=new StaffDirectoryOptions{Enabled=true,Provider=entra?"Entra":"Keycloak",ClientId="directory-reader",TenantId=Tenant,KeycloakSubjectMappingVerified=true,ClientSecretName="approved/directory-client"};
        var staff=new StaffOidcOptions{Enabled=true,ProviderId="staff",Authority=entra?$"https://login.microsoftonline.com/{Tenant}/v2.0":"https://identity.example/auth/realms/staff",AllowedTenantIds=entra?[Tenant]:[]};
        var handler=new Handler(); var secrets=new Secrets();
        var service=new HttpStaffDirectoryLookup(new HttpClient(handler),Microsoft.Extensions.Options.Options.Create(options),Microsoft.Extensions.Options.Options.Create(staff),secrets);
        return (service,options,staff,handler,secrets);
    }
    private static void Respond(Handler handler,string json,HttpStatusCode status=HttpStatusCode.OK)
    {
        handler.Responses.Enqueue((HttpStatusCode.OK,"{\"access_token\":\"directory-token\"}"));
        handler.Responses.Enqueue((status,json));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task VerifiedDirectoryIdentityMatchesActualOidcIdentityKey(bool entra)
    {
        var f=Setup(entra);
        Respond(f.Handler,entra?$"{{\"id\":\"{ObjectId}\",\"userPrincipalName\":\"user@example.org\",\"displayName\":\"Directory User\",\"mail\":\"user@example.org\",\"accountEnabled\":true}}"
            : "{\"id\":\"f:ldap:immutable-user\",\"username\":\"user@example.org\",\"firstName\":\"Directory\",\"lastName\":\"User\",\"email\":\"user@example.org\",\"enabled\":true}");
        var user=await f.Service.FindAsync(entra?ObjectId:"f:ldap:immutable-user",TestContext.Current.CancellationToken);
        Assert.NotNull(user); Assert.True(user.Enabled); Assert.Equal("Directory User",user.DisplayName);
        var claims=new List<Claim>{new("sub",entra?"pairwise-sub-is-not-the-object-id":"f:ldap:immutable-user"),new("preferred_username","user@example.org")};
        if(entra){ claims.Add(new("oid",ObjectId)); claims.Add(new("tid",Tenant)); }
        var proof=VerifiedExternalIdentityFactory.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(claims,"validated-oidc")),
            new("staff",f.Staff.Authority,entra?AuthenticationProviderKind.EntraId:AuthenticationProviderKind.External,f.Staff.AllowedTenantIds));
        Assert.Equal(proof.Key,user.Key); Assert.Equal(proof.ProviderKind,user.Kind);
        Assert.Equal("approved/directory-client",f.Secrets.Name);
        Assert.Equal("POST",f.Handler.Requests[0].Method);
        Assert.Contains("grant_type=client_credentials",f.Handler.Requests[0].Body);
        Assert.Equal("GET",f.Handler.Requests[1].Method); Assert.Equal("directory-token",f.Handler.Requests[1].Bearer);
        Assert.DoesNotContain("directory-client-secret",f.Handler.Requests[1].Url);
        if(entra) Assert.StartsWith("https://graph.microsoft.com/v1.0/users/",f.Handler.Requests[1].Url);
        else Assert.StartsWith("https://identity.example/auth/admin/realms/staff/users/",f.Handler.Requests[1].Url);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SearchesAreBoundedAndEscapedAndDisabledUsersRemainDisabled(bool entra)
    {
        var f=Setup(entra);
        string row=entra?$"{{\"id\":\"{ObjectId}\",\"userPrincipalName\":\"user@example.org\",\"displayName\":\"User\",\"accountEnabled\":false}}"
            : "{\"id\":\"user-id\",\"username\":\"user@example.org\",\"enabled\":false}";
        Respond(f.Handler,entra?"{\"value\":["+row+"]}":"["+row+"]");
        var users=await f.Service.SearchAsync("O'Neil & staff",TestContext.Current.CancellationToken);
        Assert.False(Assert.Single(users).Enabled);
        string url=Uri.UnescapeDataString(f.Handler.Requests[1].Url);
        if(entra){ Assert.Contains("O''Neil & staff",url); Assert.Contains("$top=21",url); }
        else Assert.Contains("max=21",url);
        Assert.Equal(2,f.Handler.Requests.Count);
    }

    [Theory]
    [InlineData("disabled")] [InlineData("http")] [InlineData("unverified-mapping")] [InlineData("mixed-provider")] [InlineData("different-issuer")] [InlineData("missing-client")]
    public async Task UnsafeOrIncompleteConfigurationMakesNoRemoteRequests(string mode)
    {
        var f=Setup();
        switch(mode){
            case "disabled":f.Options.Enabled=false;break;
            case "http":f.Staff.Authority="http://identity.example/realms/staff";break;
            case "unverified-mapping":f.Options.KeycloakSubjectMappingVerified=false;break;
            case "mixed-provider":f.Staff.AllowedTenantIds=[Tenant];break;
            case "different-issuer":f.Staff.Issuer="https://other.example/realms/staff";break;
            case "missing-client":f.Options.ClientId="";break;
        }
        Assert.NotNull(f.Service.UnavailableReason);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.SearchAsync("user",TestContext.Current.CancellationToken));
        Assert.Empty(f.Handler.Requests); Assert.Null(f.Secrets.Name);
    }

    [Fact]
    public async Task EntraMustMatchAllowedTenantAndAuthority()
    {
        var f=Setup(true); f.Staff.AllowedTenantIds=[Guid.NewGuid().ToString("D")]; Assert.NotNull(f.Service.UnavailableReason);
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
        Assert.Null(await f.Service.FindAsync("missing",TestContext.Current.CancellationToken));
        Respond(f.Handler,"sensitive provider response",HttpStatusCode.Forbidden);
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.FindAsync("forbidden",TestContext.Current.CancellationToken));
        Assert.Contains("HTTP 403",error.Message); Assert.DoesNotContain("sensitive",error.Message);
    }

    [Theory] [InlineData("..")][InlineData("user/other")][InlineData("user\\other")]
    public async Task InvalidIdsCannotChangeProviderEndpoint(string id)
    {
        var f=Setup();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.FindAsync(id,TestContext.Current.CancellationToken));
        Assert.Empty(f.Handler.Requests);
    }
}
