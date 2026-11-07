using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Common.Storage;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class DashboardStorageDiagnosticTests
{
    public class Storage : DispatchProxy
    {
        public bool Missing;
        public int Reads;
        protected override object? Invoke(MethodInfo? method,object?[]? args) => method!.Name switch
        {
            "GetMetadataAsync" => Task.FromResult<StoredFile?>(Missing ? null : new("fixture",1,"application/json","fixture","",DateTimeOffset.UtcNow,"fixture",1,StorageStatus.Stored,new Dictionary<string,string>())),
            "OpenReadAsync" => Fail(),
            _ => throw new InvalidOperationException("The probe attempted a non-read operation.")
        };
        private Task<Stream> Fail() {Reads++;throw new StorageException("STORAGE-SHAREPOINT-READ-001","PRIVATE key, PRIVATE token {\"error\":{\"code\":\"activityLimitReached\",\"message\":\"PRIVATE\"}}");}
    }
    private sealed class ResponseHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;
            var response=new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter=new RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
            return Task.FromResult(response);
        }
    }
    [Fact]
    public void FailureClassificationNeverExportsMessagesKeysTokensOrUnknownCodes()
    {
        var failure=DashboardStorageDiagnostics.Classify(new StorageException("STORAGE-SHAREPOINT-METADATA-001","PRIVATE {\"code\":\"accessDenied\",\"message\":\"PRIVATE\"}"),"metadata");
        Assert.Equal("STORAGE-SHAREPOINT-METADATA-001",failure.StorageCode);Assert.Equal("accessDenied",failure.GraphCode);
        Assert.DoesNotContain("PRIVATE",System.Text.Json.JsonSerializer.Serialize(failure));
        Assert.Null(DashboardStorageDiagnostics.Classify(new StorageException("STORAGE-PRIVATE credential","PRIVATE"),"read").StorageCode);
    }
    [Fact]
    public async Task ProbeStopsAtFirstFailureAndNeverWritesOrCalculatesAnAggregate()
    {
        var storage=(Storage)DispatchProxy.Create<IFileStorage,Storage>();
        var result=await DashboardStorageDiagnostics.ProbeAsync((IFileStorage)storage,Enumerable.Range(0,100).Select(_=>Guid.NewGuid()).ToArray(),1,TestContext.Current.CancellationToken);
        Assert.True(result.StoppedAfterFailure);Assert.Equal(1,result.Examined);Assert.Equal(1,storage.Reads);
        var failure=Assert.Single(result.Failures);Assert.Equal("read",failure.Operation);Assert.Equal("activityLimitReached",failure.GraphCode);
    }
    [Fact]
    public async Task MissingLegacyLocationsAreReportedWithoutOpeningContent()
    {
        var storage=(Storage)DispatchProxy.Create<IFileStorage,Storage>();storage.Missing=true;
        var result=await DashboardStorageDiagnostics.ProbeAsync((IFileStorage)storage,[Guid.NewGuid(),Guid.NewGuid()],1,TestContext.Current.CancellationToken);
        Assert.Equal(2,result.Missing);Assert.Equal(0,storage.Reads);Assert.Empty(result.Failures);
    }
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task DiagnosticTransportBlocksGraphWrites(string method)
    {
        var inner=new ResponseHandler();using var client=new HttpClient(new ReadOnlyStorageHttpHandler(inner));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>client.SendAsync(new(new HttpMethod(method),"https://graph.microsoft.com/v1.0/fixture"),TestContext.Current.CancellationToken));
        Assert.Equal(0,inner.Calls);
    }
    [Fact]
    public async Task DiagnosticTransportRecordsOnlyStatusAndRetryAfter()
    {
        var inner=new ResponseHandler();var handler=new ReadOnlyStorageHttpHandler(inner);using var client=new HttpClient(handler);
        using var response=await client.GetAsync("https://graph.microsoft.com/v1.0/fixture",TestContext.Current.CancellationToken);
        Assert.Equal(1,handler.Statuses[429]);Assert.Equal(12,handler.RetryAfterSeconds);
        using var token=await client.PostAsync("https://login.microsoftonline.com/fixture/oauth2/v2.0/token",null,TestContext.Current.CancellationToken);
        Assert.Equal(2,inner.Calls);
    }
}
