using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Common.Storage;
using RequestPortal.StorageMigration;
using RequestPortal.Infrastructure.Storage;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class NetworkFolderMigrationTests
{
    private sealed class Workspace : IDisposable
    {
        public string Root {get;}=Path.Combine(Path.GetTempPath(),"rp-migration-tests-"+Guid.NewGuid().ToString("N"));
        public Workspace()=>Directory.CreateDirectory(Root);
        public string PathFor(string name)=>Path.Combine(Root,name);
        public void Dispose()=>Directory.Delete(Root,true);
    }
    private static StoredFile Metadata(byte[] payload,int version=1)=>new("Requests/RP-1/Supporting Documents/example.pdf",payload.Length,"application/pdf","example.pdf",
        Convert.ToHexStringLower(SHA256.HashData(payload)),new DateTimeOffset(2026,9,1,0,0,0,TimeSpan.Zero),"original-uploader",version,StorageStatus.Stored,
        new Dictionary<string,string>{{"Application","RequestPortal"},{"OwnerId","original-association"},{"StorageProvider","SharePoint"},{"Custom","preserved"}});
    [Fact]
    public async Task ImportPreservesKeysMetadataVersionsAndSupportsFutureNetworkUploadsDownloadsAndDelete()
    {
        var ct=TestContext.Current.CancellationToken;using var work=new Workspace();string root=work.PathFor("files");Directory.CreateDirectory(root);
        byte[] first="original first version"u8.ToArray(),second="original second version"u8.ToArray();
        string source1=work.PathFor("one"),source2=work.PathFor("two");await File.WriteAllBytesAsync(source1,first,ct);await File.WriteAllBytesAsync(source2,second,ct);
        var meta1=Metadata(first);var meta2=Metadata(second,2);var current=meta2 with {Metadata=new Dictionary<string,string>(meta2.Metadata){{"LaterTag","preserved-current-only"}}};
        var importer=new NetworkFolderImporter(root);
        await importer.ImportVersionAsync(meta1,source1,ct);await importer.ImportVersionAsync(meta2,source2,ct);await importer.PublishCurrentAsync(current,source2,ct);
        // Re-running an interrupted or completed import is idempotent; no version counters are advanced.
        await importer.ImportVersionAsync(meta1,source1,ct);await importer.PublishCurrentAsync(current,source2,ct);
        var provider=new NetworkFolderStorage(root);var read=await provider.GetMetadataAsync(current.StorageKey,ct);
        Assert.NotNull(read);Assert.Equal(NetworkFolderImporter.MetadataFingerprint(current),NetworkFolderImporter.MetadataFingerprint(read));
        Assert.Equal(2,(await provider.GetVersionsAsync(current.StorageKey,ct)).Count);
        await using(var stream=await provider.OpenVersionAsync(meta1.StorageKey,1,ct))
            Assert.Equal(meta1.Sha256,Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,ct)));
        await using var third=new MemoryStream("new application upload"u8.ToArray());
        var updated=await provider.StoreAsync(new(current.StorageKey,third,"application/pdf","example.pdf","permitted-application"),ct);
        Assert.Equal(3,updated.Version);
        await using(var stream=await provider.OpenReadAsync(current.StorageKey,ct))
            Assert.Equal(updated.Sha256,Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,ct)));
        await provider.DeleteAsync(current.StorageKey,ct);Assert.Null(await provider.GetMetadataAsync(current.StorageKey,ct));
        Assert.Equal(first,await File.ReadAllBytesAsync(source1,ct)); // Source is never altered or deleted.
    }
    [Fact]
    public async Task HashMismatchCannotPublishAFileOrMetadata()
    {
        var ct=TestContext.Current.CancellationToken;using var work=new Workspace();string root=work.PathFor("files");Directory.CreateDirectory(root);
        string source=work.PathFor("corrupted");await File.WriteAllTextAsync(source,"wrong bytes",ct);
        var error=await Assert.ThrowsAsync<MigrationSourceException>(()=>new NetworkFolderImporter(root).PublishCurrentAsync(Metadata("expected bytes"u8.ToArray()),source,ct));
        Assert.Equal("SOURCE_SHA256_MISMATCH",error.Code);Assert.Empty(Directory.EnumerateFileSystemEntries(root,"*",SearchOption.AllDirectories));
        Assert.True(File.Exists(source));
    }
    [Theory]
    [InlineData("../unrelated.pdf")]
    [InlineData("/outside.pdf")]
    [InlineData("folder/../../outside")]
    [InlineData("folder\\file")]
    [InlineData("folder//file")]
    public void UnsafeKeysCannotEscapeTheDedicatedRoot(string key)=>Assert.Throws<MigrationSourceException>(()=>NetworkFolderImporter.ValidateKey(key));
    [Fact]
    public void ProviderSidecarAndCaseCollisionsAreRejectedBeforeAnyCopy()
    {
        var meta=Metadata([1]);
        Assert.Throws<MigrationSourceException>(()=>NetworkFolderImporter.ValidateInventory([meta,meta with {StorageKey=meta.StorageKey+".json"}]));
        Assert.Throws<MigrationSourceException>(()=>NetworkFolderImporter.ValidateInventory([meta,meta with {StorageKey=meta.StorageKey.ToUpperInvariant()}]));
    }
    [Fact]
    public async Task ExistingUnrelatedDestinationContentIsNotOverwritten()
    {
        var ct=TestContext.Current.CancellationToken;using var work=new Workspace();string root=work.PathFor("files");Directory.CreateDirectory(root);
        var meta=Metadata("new content"u8.ToArray());string path=Path.Combine(root,meta.StorageKey);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path,"existing unrelated content",ct);string input=work.PathFor("source");await File.WriteAllBytesAsync(input,"new content"u8.ToArray(),ct);
        await Assert.ThrowsAsync<MigrationSourceException>(()=>new NetworkFolderImporter(root).PublishCurrentAsync(meta,input,ct));
        Assert.Equal("existing unrelated content",await File.ReadAllTextAsync(path,ct));
    }
    [Theory]
    [InlineData("cifs","//nas01.fftpj.local/ffpmanager_data","/Supporting\\040Document/RequestPortal/files",true)]
    [InlineData("ext4","/dev/sda1","/",false)]
    [InlineData("cifs","//other/share","/Supporting\\040Document/RequestPortal/files",false)]
    [InlineData("cifs","//nas01.fftpj.local/ffpmanager_data","/Supporting\\040Document",false)]
    public void MountIdentityAndSubdirectoryMustMatchWithoutLocalFallback(string type,string source,string mountRoot,bool valid)
    {
        string info=$"10 1 0:1 {mountRoot} /var/lib/aegis/requestportal/supporting-documents rw,nosuid - {type} {source} rw\n";
        void Check()=>NetworkFolderMountGuard.Verify("/var/lib/aegis/requestportal/supporting-documents","//nas01.fftpj.local/ffpmanager_data","/Supporting Document/RequestPortal/files",info);
        if(valid)Check();else Assert.Throws<InvalidOperationException>(Check);
    }
    private sealed class ThrottleHandler : HttpMessageHandler
    {
        public int Reads;
        public List<HttpMethod> Methods=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Methods.Add(request.Method);
            if(request.Method==HttpMethod.Post)return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"access_token\":\"fixture-only\",\"expires_in\":3600}")});
            Reads++;
            if(Reads==1) {var response=new HttpResponseMessage(HttpStatusCode.TooManyRequests);response.Headers.RetryAfter=new RetryConditionHeaderValue(TimeSpan.FromSeconds(12));return Task.FromResult(response);}
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"value\":[]}")});
        }
    }
    [Fact]
    public async Task SharePointBackoffHonorsRetryAfterPersistsItAndNeverWritesSource()
    {
        var ct=TestContext.Current.CancellationToken;using var work=new Workspace();var handler=new ThrottleHandler();using var http=new HttpClient(handler);
        var waits=new List<TimeSpan>();var options=new SharePointStorageOptions("tenant","client","fixture-secret","site",null,"",DriveId:"drive",RootFolder:"Portal");
        var graph=new GraphReadClient(http,options,work.Root,(duration,_)=>{waits.Add(duration);return Task.CompletedTask;},TimeSpan.Zero);
        using var response=await graph.JsonAsync("https://graph.microsoft.com/v1.0/drives/drive/root",ct);
        Assert.Equal(1,graph.ThrottledResponses);Assert.Contains(waits,x=>x>=TimeSpan.FromSeconds(12));Assert.True(File.Exists(work.PathFor("throttle.json")));
        Assert.All(handler.Methods,method=>Assert.True(method==HttpMethod.Get || method==HttpMethod.Post));
        await Assert.ThrowsAsync<MigrationSourceException>(()=>graph.JsonAsync("https://unapproved.invalid/private",ct));
    }
}
