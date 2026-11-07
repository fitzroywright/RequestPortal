using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Storage;
using RequestPortal.Application.Management;
using RequestPortal.LocationRecovery;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class RequestLocationExportTests
{
    private sealed class Source : HttpMessageHandler
    {
        public Guid Id = Guid.NewGuid();
        public bool Corrupt, MissingApplicationRoot, MissingGeographyRoot, WrongMetadataOwner;
        public int PayloadReads;
        public string State { get; } = Directory.CreateTempSubdirectory("rp-geography-source-").FullName;
        public bool ThrottleOnce;
        public bool ConcurrentUpdate;
        public string ContentETag = "stable";
        public byte[] Payload => JsonSerializer.SerializeToUtf8Bytes(new RequestLocationSnapshot(Id, "Half Way Tree, St. Andrew", "St. Andrew", "Half Way Tree",
            18.0179123456789m, -76.7936123456789m, LocationPrecision.Exact, DateTimeOffset.Parse("2026-09-01T00:00:00Z")), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("login.microsoftonline.com", request.RequestUri!.Host);
                return Task.FromResult(Json(new { access_token = "fixture", expires_in = 3600 }));
            }
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("graph.microsoft.com", request.RequestUri!.Host);
            if (ThrottleOnce)
            {
                ThrottleOnce = false;
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
                return Task.FromResult(response);
            }
            string path = Uri.UnescapeDataString(request.RequestUri.AbsolutePath);
            if (path.EndsWith("root:/Portal", StringComparison.Ordinal))
                return Task.FromResult(MissingApplicationRoot ? new(HttpStatusCode.NotFound) : Json(new { id = "app", folder = new { } }));
            if (path.EndsWith("root:/Portal/requestportal/locations:/children", StringComparison.Ordinal))
                return Task.FromResult(MissingGeographyRoot ? new(HttpStatusCode.NotFound) : Json(new { value = new[] { new { id = "geo", name = Id.ToString("N"), folder = new { } } } }));
            if (path.EndsWith("/items/geo/children", StringComparison.Ordinal))
                return Task.FromResult(Json(new { value = new[] { new { id = "payload", name = "resolved-location.json", eTag = ContentETag, size = Payload.Length, file = new { } } } }));
            string key = $"requestportal/locations/{Id:N}/resolved-location.json";
            string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
            if (path.EndsWith("/.common-storage/metadata/" + digest + ".json:/content", StringComparison.Ordinal))
                return Task.FromResult(Json(new StoredFile(key, Payload.Length, "application/json", "resolved-location.json", Convert.ToHexStringLower(SHA256.HashData(Payload)),
                    DateTimeOffset.UtcNow, "legacy", 1, StorageStatus.Stored, new Dictionary<string, string> { { "RequestId", (WrongMetadataOwner ? Guid.NewGuid() : Id).ToString("D") } })));
            if (path.EndsWith("/items/payload/content", StringComparison.Ordinal))
            {
                if (ConcurrentUpdate) ContentETag = "changed";
                PayloadReads++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Corrupt ? "corrupt"u8.ToArray() : Payload) });
            }
            if (path.EndsWith("/items/payload", StringComparison.Ordinal)) return Task.FromResult(Json(new { eTag = ContentETag }));
            throw new InvalidOperationException("Unexpected route; supporting documents must not be enumerated or read.");
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && Directory.Exists(State)) Directory.Delete(State, true);
            base.Dispose(disposing);
        }
    }
    private static SharePointLocationExport Reader(Source source, string state, out HttpClient http)
    {
        var options = new SharePointStorageOptions("tenant", "client", "fixture", "site", null, "", DriveId: "drive", RootFolder: "Portal");
        http = new HttpClient(source, false);
        return new(new GraphReadClient(http, options, source.State, (_, _) => Task.CompletedTask, TimeSpan.Zero), options);
    }
    [Fact]
    public async Task OnlyGeographyIsExportedAndUnchangedVerifiedPayloadsAreReusedForTheFinalFence()
    {
        var ct = TestContext.Current.CancellationToken; using var source = new Source();
        var reader = Reader(source, Path.GetTempPath(), out var http); using var client = http;
        var first = await reader.ReadAsync(new HashSet<Guid> { source.Id }, false, null, ct);
        Assert.False(first.WritersStopped); Assert.True(first.Complete); Assert.Equal(source.Payload, Assert.Single(first.Records).Location.Payload);
        var final = await reader.ReadAsync(new HashSet<Guid> { source.Id }, true, first, ct);
        Assert.True(final.WritersStopped); Assert.Equal(1, source.PayloadReads);
        Assert.Equal(source.Payload, Assert.Single(final.Records).Location.Payload);
    }
    [Fact]
    public async Task CorruptGeographyIsStoppedAtTheExactShaVerificationOperation()
    {
        using var source = new Source { Corrupt = true };
        var reader = Reader(source, Path.GetTempPath(), out var http); using var client = http;
        var error = await Assert.ThrowsAsync<LocationSourceException>(() => reader.ReadAsync(new HashSet<Guid> { source.Id }, false, null, TestContext.Current.CancellationToken));
        Assert.Equal("STORAGE-CORRUPT-001", error.Code); Assert.Equal("verify-location-sha256", reader.Operation);
        Assert.NotNull(reader.ExpectedSha256); Assert.NotEqual(reader.ExpectedSha256, reader.ActualSha256);
    }
    [Fact]
    public async Task ConcurrentSourceWriteIsDistinguishedFromAStableIntegrityMismatch()
    {
        using var source = new Source { Corrupt = true, ConcurrentUpdate = true };
        var reader = Reader(source, source.State, out var http); using var client = http;
        var error = await Assert.ThrowsAsync<LocationSourceException>(() => reader.ReadAsync(new HashSet<Guid> { source.Id }, false, null, TestContext.Current.CancellationToken));
        Assert.Equal("LOCATION_CHANGED_DURING_EXPORT", error.Code); Assert.Equal(1, source.PayloadReads);
    }
    [Fact]
    public async Task MissingApplicationRootCannotMasqueradeAsEmptyGeographyHistory()
    {
        using var source = new Source { MissingApplicationRoot = true };
        var reader = Reader(source, Path.GetTempPath(), out var http); using var client = http;
        await Assert.ThrowsAsync<LocationSourceException>(() => reader.ReadAsync(new HashSet<Guid> { source.Id }, false, null, TestContext.Current.CancellationToken));
        Assert.Equal("verify-application-folder", reader.Operation); Assert.Equal(0, source.PayloadReads);
    }
    [Fact]
    public async Task VerifiedApplicationWithNoGeographyFolderProducesAnExplicitEmptyExport()
    {
        using var source = new Source { MissingGeographyRoot = true };
        var reader = Reader(source, Path.GetTempPath(), out var http); using var client = http;
        var result = await reader.ReadAsync(new HashSet<Guid> { source.Id }, true, null, TestContext.Current.CancellationToken);
        Assert.True(result.Complete); Assert.Empty(result.Records); Assert.Equal(0, source.PayloadReads);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrphanedOrMismatchedGeographyCannotBeSilentlyImported(bool wrongOwner)
    {
        using var source = new Source { WrongMetadataOwner = wrongOwner };
        var reader = Reader(source, Path.GetTempPath(), out var http); using var client = http;
        var error = await Assert.ThrowsAsync<LocationSourceException>(() => reader.ReadAsync(wrongOwner ? new HashSet<Guid> { source.Id } : new HashSet<Guid>(), false, null, TestContext.Current.CancellationToken));
        Assert.Equal(wrongOwner ? "LOCATION_METADATA_IDENTITY_MISMATCH" : "ORPHAN_LOCATION_REQUIRES_REVIEW", error.Code);
        Assert.Equal(0, source.PayloadReads);
    }
    [Fact]
    public async Task GeographyReadsHonorAndPersistProviderRetryAfterWithoutSourceWrites()
    {
        using var source = new Source { ThrottleOnce = true };
        var reader = Reader(source, source.State, out var http); using var client = http;
        var result = await reader.ReadAsync(new HashSet<Guid> { source.Id }, false, null, TestContext.Current.CancellationToken);
        Assert.True(result.Complete);
        var notBefore = JsonSerializer.Deserialize<DateTimeOffset>(await File.ReadAllTextAsync(Path.Combine(source.State, "throttle.json"), TestContext.Current.CancellationToken));
        Assert.True(notBefore > DateTimeOffset.UtcNow);
    }
}
