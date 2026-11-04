using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;
using RequestPortal.Infrastructure.Integration;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Security;
using Xunit;
using Proxy = RequestPortal.Infrastructure.Tests.MessageNavigationPageTests.Proxy;

namespace RequestPortal.Infrastructure.Tests;

public sealed class IntegrationSecurityContractTests
{
    [Theory]
    [InlineData(true,true,true,true)]
    [InlineData(false,true,true,true)]
    [InlineData(true,false,true,true)]
    [InlineData(true,true,false,true)]
    [InlineData(true,true,true,false)]
    public async Task RecoveryRequiresStaffSurfaceAccessAdministratorPermissionAndExactActor(bool staff,bool access,bool admin,bool sameActor)
    {
        var id=Guid.NewGuid();var claims=new List<Claim>{new(ClaimTypes.NameIdentifier,id.ToString())};
        if(staff)claims.Add(new(StaffOidcOptions.StaffSurfaceClaim,StaffOidcOptions.StaffSurfaceValue));
        if(access)claims.Add(new(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.Access));
        if(admin)claims.Add(new(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.ManageSecurity));
        var context=new DefaultHttpContext{User=new(new ClaimsIdentity(claims,"test"))};
        var authorization=new IntegrationOperatorAuthorization(new HttpContextAccessor{HttpContext=context});
        if(staff&&access&&admin&&sameActor)await authorization.DemandAsync(id,TestContext.Current.CancellationToken);
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>authorization.DemandAsync(sameActor?id:Guid.NewGuid(),TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("SQL_AUTH")][InlineData("SQL_CONSTRAINT")][InlineData("SQL_CONNECTION")][InlineData("DESTINATION_CONFLICT")]
    [InlineData("SQL_547")][InlineData("SQL_18456")][InlineData("SQL_1205")]
    public async Task BridgeClientRetainsSafeErrorCodeWithoutRemoteExceptionOrCredentialPayload(string code)
    {
        using var http=new HttpClient(new Handler(_=>new(HttpStatusCode.UnprocessableEntity){Content=JsonContent.Create(
            new TransferFailure("SqlServer","Unsafe",code,"TOP-SECRET password=TOP-SECRET",false))})){BaseAddress=new("https://bridge.invalid/")};
        var client=Client(http);
        var error=await Assert.ThrowsAsync<IntegrationTransferException>(()=>client.FindApplicationAsync(Guid.NewGuid(),TestContext.Current.CancellationToken));
        Assert.Equal(code,error.Failure.Code);Assert.DoesNotContain("TOP-SECRET",error.ToString());
    }

    [Theory][InlineData(404)][InlineData(401)][InlineData(500)]
    public async Task MissingLookupRouteAndUnauthorizedResponsesAreFailuresNotProofOfMissingApplication(int status)
    {
        using var http=new HttpClient(new Handler(_=>new((HttpStatusCode)status){Content=new StringContent("TOP-SECRET") })){BaseAddress=new("https://bridge.invalid/")};
        var error=await Assert.ThrowsAsync<HttpRequestException>(()=>Client(http).FindApplicationAsync(Guid.NewGuid(),TestContext.Current.CancellationToken));
        Assert.Equal((HttpStatusCode)status,error.StatusCode);Assert.DoesNotContain("TOP-SECRET",error.Message);
    }
    [Fact]
    public async Task OnlyExplicitNoContentMeansDestinationWasNotFound()
    {
        using var http=new HttpClient(new Handler(request=>{
            Assert.Equal(HttpMethod.Get,request.Method);Assert.Contains("/by-request/",request.RequestUri!.AbsolutePath);
            Assert.True(request.Headers.Contains("X-FFP-Integration-Key"));return new(HttpStatusCode.NoContent);
        })){BaseAddress=new("https://bridge.invalid/")};
        Assert.Null(await Client(http).FindApplicationAsync(Guid.NewGuid(),TestContext.Current.CancellationToken));
    }
    private static FfpManagerBridgeClient Client(HttpClient http)=>new(http,Options.Create(new FfpManagerBridgeOptions()),
        Proxy.For<global::Common.Secrets.ISecretProvider>((method,_)=>method=="GetAsync"?Task.FromResult<string?>("TEST-ONLY-CREDENTIAL"):throw new InvalidOperationException(method)));
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>Task.FromResult(response(request)); }
}
