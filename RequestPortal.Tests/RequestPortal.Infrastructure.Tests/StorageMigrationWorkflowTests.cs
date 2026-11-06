using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Storage;
using RequestPortal.StorageMigration;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class StorageMigrationWorkflowTests
{
    private sealed class Fixture : HttpMessageHandler,IDisposable
    {
        public string Root {get;}=Path.Combine(Path.GetTempPath(),"rp-source-tests-"+Guid.NewGuid().ToString("N"));
        public byte[] V1="first valid payload"u8.ToArray(),V2="second valid payload"u8.ToArray();
        public bool CorruptCurrent,MissingContent;
        public bool ShortCorruptContent;
        public string ContentETag="content";
        public int BlobDownloads;
        public readonly string Key="Requests/RP-1/Supporting Documents/document.pdf";
        public StoredFile Metadata(int version)=>new(Key,(version==1?V1:V2).Length,"application/pdf","document.pdf",
            Convert.ToHexStringLower(SHA256.HashData(version==1?V1:V2)),DateTimeOffset.Parse("2026-09-01T00:00:00Z"),"original",version,
            StorageStatus.Stored,new Dictionary<string,string>{{"Application","RequestPortal"}});
        public Fixture()=>Directory.CreateDirectory(Root);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            string path=Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value))};
            HttpResponseMessage Bytes(byte[] value)=>new(HttpStatusCode.OK){Content=new ByteArrayContent(value)};
            if(request.Method==HttpMethod.Post)
            {
                Assert.Equal("login.microsoftonline.com",request.RequestUri.Host);
                return Task.FromResult(Json(new {access_token="fixture-only",expires_in=3600}));
            }
            Assert.Equal(HttpMethod.Get,request.Method);Assert.Equal("graph.microsoft.com",request.RequestUri.Host);
            byte[] actual=CorruptCurrent?(ShortCorruptContent?"bad"u8.ToArray():"corrupted source bytes"u8.ToArray()):V2;
            string digest=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Key)));
            object Folder(string id,string name)=>new {id,name,eTag=id,size=0,folder=new {}};
            object File(string id,string name,long size)=>new {id,name,eTag=id=="content"?ContentETag:id,size,file=new {}};
            if(path.EndsWith("root:/Portal",StringComparison.Ordinal))return Task.FromResult(Json(new {id="root"}));
            if(path.Contains("/children",StringComparison.Ordinal))
            {
                string id=path.Split('/')[^2];object[] values=id switch
                {
                    "root"=>[Folder("requests","Requests"),Folder("control",".common-storage")],
                    "requests"=>[Folder("ref","RP-1")],"ref"=>[Folder("support","Supporting Documents")],
                    "support"=>MissingContent?[]:[File("content","document.pdf",actual.Length)],
                    "control"=>[Folder("metadata","metadata"),Folder("versions","versions")],
                    "versions"=>[Folder("version-key",digest)],
                    "version-key"=>[File("meta-v1","00000001.json",JsonSerializer.SerializeToUtf8Bytes(Metadata(1)).Length),File("meta-v2","00000002.json",JsonSerializer.SerializeToUtf8Bytes(Metadata(2)).Length)],
                    "metadata"=>[File("meta-current",digest+".json",JsonSerializer.SerializeToUtf8Bytes(Metadata(2)).Length)],
                    _=>throw new InvalidOperationException("Unexpected fixture folder")
                };
                if(id=="metadata" && !request.RequestUri.Query.Contains("page=2",StringComparison.Ordinal))
                    return Task.FromResult(Json(new Dictionary<string,object>{{"value",Array.Empty<object>()},{"@odata.nextLink","https://graph.microsoft.com/v1.0/drives/d/items/metadata/children?page=2"}}));
                return Task.FromResult(Json(new {value=values}));
            }
            if(path.EndsWith("/versions",StringComparison.Ordinal))
            {
                object Version(string id,byte[] bytes)=>new {id,size=bytes.Length,lastModifiedDateTime="2026-09-01T00:00:00Z"};
                return Task.FromResult(Json(new {value=CorruptCurrent?new[] {Version("3.0",actual),Version("2.0",V2),Version("1.0",V1)}:new[] {Version("2.0",V2),Version("1.0",V1)}}));
            }
            if(path.EndsWith("/content",StringComparison.Ordinal) && !path.EndsWith("/items/content",StringComparison.Ordinal))
            {
                if(path.Contains("/meta-",StringComparison.Ordinal))return Task.FromResult(Json(Metadata(path.Contains("meta-v1",StringComparison.Ordinal)?1:2)));
                BlobDownloads++;
                if(path.Contains("/versions/1.0/",StringComparison.Ordinal))return Task.FromResult(Bytes(V1));
                if(path.Contains("/versions/2.0/",StringComparison.Ordinal))return Task.FromResult(Bytes(V2));
                return Task.FromResult(Bytes(actual));
            }
            string itemId=path.Split('/')[^1];
            return Task.FromResult(Json(new {eTag=itemId=="content"?ContentETag:itemId}));
        }
        public (SharePointSource Source,HttpClient Http) Source()
        {
            var http=new HttpClient(this,false);
            var options=new SharePointStorageOptions("tenant","client","fixture","site",null,"",DriveId:"d",RootFolder:"Portal");
            var graph=new GraphReadClient(http,options,Root,(_,_)=>Task.CompletedTask,TimeSpan.Zero);
            return (new(graph,options),http);
        }
        protected override void Dispose(bool disposing) {if(disposing && Directory.Exists(Root))Directory.Delete(Root,true);base.Dispose(disposing);}
    }
    [Fact]
    public async Task PaginatedInventoryResumableCopyAndFullVersionReconciliationPreserveAllReferences()
    {
        var ct=TestContext.Current.CancellationToken;using var fixture=new Fixture();var (source,http)=fixture.Source();using var client=http;
        var inventory=await source.InventoryAsync(ct);Assert.Single(inventory.Files);Assert.Equal(2,inventory.Files[0].Versions.Count);
        string destination=Path.Combine(fixture.Root,"target","files");Directory.CreateDirectory(destination);
        var engine=new MigrationEngine(source,fixture.Root,destination);
        var result=await engine.CopyAsync(inventory,ct);Assert.Equal("Verified",result.Files[fixture.Key].Status);
        int downloads=fixture.BlobDownloads;
        var resumed=await engine.CopyAsync(inventory,ct);Assert.Equal("Verified",resumed.Files[fixture.Key].Status);Assert.Equal(downloads,fixture.BlobDownloads);
        Assert.True(await MigrationEngine.VerifyAsync(new NetworkFolderStorage(destination),inventory.Files[0],ct));
    }
    [Fact]
    public async Task SourceProviderMetadataAndNativeHistoryAreArchivedAndReverifiedOutsideActiveFiles()
    {
        var ct=TestContext.Current.CancellationToken;using var fixture=new Fixture();var (source,http)=fixture.Source();using var client=http;
        var inventory=await source.InventoryAsync(ct);string destination=Path.Combine(fixture.Root,"target","files");Directory.CreateDirectory(destination);
        await new MigrationEngine(source,fixture.Root,destination).CopyAsync(inventory,ct);
        string archive=Path.Combine(fixture.Root,"target","source-archive");
        var evidence=await SourceArchive.CopyAsync(source,inventory,archive,fixture.Root,destination,ct);
        Assert.Equal(3,evidence.ArchivedControlFiles);Assert.Equal(2,evidence.NativeVersions!.Count);
        Assert.True(await SourceArchive.VerifyAsync(inventory,archive,destination,source,ct));
        int reads=fixture.BlobDownloads;
        await SourceArchive.CopyAsync(source,inventory,archive,fixture.Root,destination,ct);
        Assert.Equal(reads,fixture.BlobDownloads);
        Assert.False(Directory.Exists(Path.Combine(destination,".common-storage")));
        string checkpoint=Path.Combine(archive,"source-archive-"+inventory.SourceFingerprint+".json");
        await File.WriteAllTextAsync(checkpoint,JsonSerializer.Serialize(evidence with {NativeVersions=[]}),ct);
        Assert.False(await SourceArchive.VerifyAsync(inventory,archive,destination,source,ct));
    }

    [Fact]
    public async Task ArchiveRejectsSymbolicLinksBeforeWritingOutsideItsScopedRoot()
    {
        var ct=TestContext.Current.CancellationToken;using var fixture=new Fixture();var (source,http)=fixture.Source();using var client=http;
        var inventory=await source.InventoryAsync(ct);
        string archive=Path.Combine(fixture.Root,"archive"),unrelated=Path.Combine(fixture.Root,"unrelated");
        Directory.CreateDirectory(archive);Directory.CreateDirectory(unrelated);
        Directory.CreateSymbolicLink(Path.Combine(archive,"objects"),unrelated);
        var error=await Assert.ThrowsAsync<MigrationSourceException>(()=>SourceArchive.CopyAsync(source,inventory,archive,fixture.Root,Path.Combine(fixture.Root,"files"),ct));
        Assert.Equal("ARCHIVE_LINK_GUARD",error.Code);Assert.Empty(Directory.EnumerateFileSystemEntries(unrelated));
    }

    [Fact]
    public async Task NativeArchiveCanResumeAcrossSourceETagChangesWithoutLosingHistoricalVersions()
    {
        var ct=TestContext.Current.CancellationToken;using var fixture=new Fixture();var (source,http)=fixture.Source();using var client=http;
        string destination=Path.Combine(fixture.Root,"target","files"),archive=Path.Combine(fixture.Root,"target","source-archive");Directory.CreateDirectory(destination);
        var inventory=await source.InventoryAsync(ct);
        await new MigrationEngine(source,fixture.Root,destination).CopyAsync(inventory,ct);
        await SourceArchive.CopyAsync(source,inventory,archive,fixture.Root,destination,ct);
        fixture.ContentETag="changed";var changed=await source.InventoryAsync(ct);
        Assert.NotEqual(inventory.SourceFingerprint,changed.SourceFingerprint);
        await new MigrationEngine(source,fixture.Root,destination).CopyAsync(changed,ct);
        var result=await SourceArchive.CopyAsync(source,changed,archive,fixture.Root,destination,ct);
        Assert.Equal(2,result.NativeVersions!.Count);
        Assert.All(result.NativeVersions,x=>Assert.Equal("changed",x.SourceContentETag));
        Assert.True(await SourceArchive.VerifyAsync(changed,archive,destination,source,ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingSourceHashMismatchIsReportedAndOnlyRecoveredUsingBytesWithTheDeclaredHash(bool shorter)
    {
        var ct=TestContext.Current.CancellationToken;using var fixture=new Fixture {CorruptCurrent=true,ShortCorruptContent=shorter};var (source,http)=fixture.Source();using var client=http;
        var inventory=await source.InventoryAsync(ct);string destination=Path.Combine(fixture.Root,"target","files");Directory.CreateDirectory(destination);
        var result=await new MigrationEngine(source,fixture.Root,destination).CopyAsync(inventory,ct);
        var record=result.Files[fixture.Key];Assert.Equal("Verified",record.Status);Assert.True(record.CurrentSourceHashMismatch);Assert.True(record.RecoveredFromHistory);
        Assert.Single(result.IntegrityMismatches);
        await using var stream=await new NetworkFolderStorage(destination).OpenReadAsync(fixture.Key,ct);
        Assert.Equal(fixture.Metadata(2).Sha256,Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,ct)));
        Assert.NotEqual(record.CurrentActualSha256,record.Metadata.Sha256);
    }
    [Fact]
    public async Task MissingSourceFilesAreBlockedWithoutInventingContentOrDeletingReferences()
    {
        var ct=TestContext.Current.CancellationToken;using var fixture=new Fixture {MissingContent=true};var (source,http)=fixture.Source();using var client=http;
        var inventory=await source.InventoryAsync(ct);string destination=Path.Combine(fixture.Root,"files");Directory.CreateDirectory(destination);
        Assert.Contains(inventory.Problems,x=>x.StartsWith("SOURCE_CONTENT_MISSING:",StringComparison.Ordinal));
        var result=await new MigrationEngine(source,fixture.Root,destination).CopyAsync(inventory,ct);
        Assert.Equal("Blocked",result.Files[fixture.Key].Status);Assert.Equal("SOURCE_CONTENT_MISSING",result.Files[fixture.Key].ErrorCode);
        Assert.Empty(Directory.EnumerateFiles(destination,"*",SearchOption.AllDirectories));
    }
    [Fact]
    public void CutoverAuthorizationRejectsChangedScopeExpiredApprovalAndUnreviewedIntegrityIssues()
    {
        string sha=new('a',64),commit=new('b',40);
        var inventory=new SourceInventory(sha,DateTimeOffset.UtcNow,"d","Portal",[],[],[],false);
        var refs=new ReferenceEvidence(sha,0,0,0,0,[]);
        var ledger=new MigrationLedger {FinalSourceFenceVerified=true,SourceFingerprint=sha};
        var plan=new CutoverPlan(1,"RequestPortalOnly",commit,"axios.fftpj.local","requestportal","d","Portal","/run/scope/files","/Supporting Document/RequestPortal",
            "/var/lib/requestportal-storage-migration/state",sha,sha,true,true,false,false,false,DateTimeOffset.UtcNow.AddHours(1),[]);
        void Verify(CutoverPlan value)=>CutoverAuthorization.Verify(value,commit,plan.StateDirectory,plan.DestinationAlias,plan.ExpectedMountRoot,inventory,refs,ledger);
        Verify(plan);
        Assert.Throws<MigrationSourceException>(()=>Verify(plan with {DeployBridge=true}));
        Assert.Throws<MigrationSourceException>(()=>Verify(plan with {DeleteSourceDocuments=true}));
        Assert.Throws<MigrationSourceException>(()=>Verify(plan with {ExpiresUtc=DateTimeOffset.UtcNow.AddMinutes(-1)}));
        Assert.Throws<MigrationSourceException>(()=>Verify(plan with {SharePointDriveId="unapproved-drive"}));
        ledger.IntegrityMismatches.Add(new("private-key",sha,new string('c',64),DateTimeOffset.UtcNow));
        Assert.Throws<MigrationSourceException>(()=>Verify(plan));
        Verify(plan with {ApprovedRecoveredIntegrityIssues=[CutoverAuthorization.IntegrityFingerprint(ledger.IntegrityMismatches[0])]});
    }
}
