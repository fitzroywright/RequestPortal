using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Storage;
using RequestPortal.Application.Management;
using RequestPortal.Infrastructure.Management;

namespace RequestPortal.LocationRecovery;

public sealed record LocationSourceRecord(LegacyRequestLocation Location, string ContentETag);
public sealed record LocationExport(bool Complete, bool WritersStopped, string DriveId, string RootFolder,
    DateTimeOffset ObservedUtc, IReadOnlyList<LocationSourceRecord> Records,
    IReadOnlyList<Guid>? VerifiedMissingRequestIds = null, int? KnownRequestCount = null);

public static class LocationExportValidation
{
    public static void ValidateCoverage(LocationExport data)
    {
        if (data.VerifiedMissingRequestIds is null || data.KnownRequestCount is null) throw new LocationSourceException("LOCATION_ABSENCE_PROOF_REQUIRED");
        var identities = new HashSet<Guid>();
        foreach (var record in data.Records)
        {
            string[] parts = record.Location.StorageKey.Split('/');
            if (parts.Length != 4 || parts[0] != "requestportal" || parts[1] != "locations" || parts[3] != "resolved-location.json" ||
                !Guid.TryParseExact(parts[2], "N", out var id) || id == Guid.Empty || !identities.Add(id))
                throw new LocationSourceException("LOCATION_EXPORT_COVERAGE_INVALID");
        }
        foreach (Guid id in data.VerifiedMissingRequestIds)
            if (id == Guid.Empty || !identities.Add(id)) throw new LocationSourceException("LOCATION_EXPORT_COVERAGE_INVALID");
        if (identities.Count != data.KnownRequestCount) throw new LocationSourceException("LOCATION_EXPORT_COVERAGE_INVALID");
    }
}

/// <summary>Reads only requestportal/locations and the metadata of those exact keys. No document inventory.</summary>
public sealed class SharePointLocationExport(GraphReadClient graph, SharePointStorageOptions options)
{
    private const string Graph = "https://graph.microsoft.com/v1.0";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Operation { get; private set; } = "resolve-drive";
    public string? CurrentKey { get; private set; }
    public string? ExpectedSha256 { get; private set; }
    public string? ActualSha256 { get; private set; }
    public long? ExpectedLength { get; private set; }
    public long? ActualLength { get; private set; }
    public Action<int>? Progress { get; init; }
    private int metadataChecks;
    private string? drive;
    private static string Encode(string path) => string.Join('/', path.Trim('/').Split('/').Select(Uri.EscapeDataString));
    private async Task<string> DriveAsync(CancellationToken ct)
    {
        if (drive is not null) return drive;
        if (!string.IsNullOrWhiteSpace(options.DriveId)) return drive = options.DriveId;
        string? site = options.SiteId;
        if (string.IsNullOrWhiteSpace(site))
        {
            using var lookup = await graph.JsonAsync($"{Graph}/sites/{Uri.EscapeDataString(options.HostName!)}:/{Encode(options.SitePath)}?$select=id", ct);
            site = lookup.RootElement.GetProperty("id").GetString();
        }
        if (string.IsNullOrWhiteSpace(options.DriveName))
        {
            using var lookup = await graph.JsonAsync($"{Graph}/sites/{Uri.EscapeDataString(site!)}/drive?$select=id", ct);
            return drive = lookup.RootElement.GetProperty("id").GetString()!;
        }
        var drives = await graph.PagesAsync($"{Graph}/sites/{Uri.EscapeDataString(site!)}/drives?$select=id,name", ct);
        return drive = drives.Single(x => string.Equals(x.GetProperty("name").GetString(), options.DriveName, StringComparison.OrdinalIgnoreCase)).GetProperty("id").GetString()!;
    }
    private string PathUrl(string relative) => $"{Graph}/drives/{Uri.EscapeDataString(drive!)}/root:/{Encode(string.Join('/', new[] { options.RootFolder.Trim('/'), relative }.Where(x => x.Length > 0)))}";
    private string ItemUrl(string id) => $"{Graph}/drives/{Uri.EscapeDataString(drive!)}/items/{Uri.EscapeDataString(id)}";
    private async Task<StoredFile?> MetadataAsync(string key, CancellationToken ct)
    {
        if (++metadataChecks % 100 == 0) Progress?.Invoke(metadataChecks);
        string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        try
        {
            using var document = await graph.JsonAsync(PathUrl(".common-storage/metadata/" + digest + ".json") + ":/content", ct);
            return document.RootElement.Deserialize<StoredFile>(Json) ?? throw new LocationSourceException("LOCATION_METADATA_INVALID");
        }
        catch (LocationSourceException error) when (error.Code == "GRAPH_HTTP_404") { return null; }
    }
    private async Task<byte[]> ContentAsync(string url, CancellationToken ct)
    {
        using var response = await graph.SendAsync(url, ct);
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream(); byte[] buffer = new byte[8192];
        while (true)
        {
            int read = await input.ReadAsync(buffer, ct); if (read == 0) break;
            if (output.Length + read > 32 * 1024) throw new LocationSourceException("LOCATION_PAYLOAD_SIZE_GUARD");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
    private async Task<LocationSourceRecord> ReadRecordAsync(Guid id, JsonElement content, StoredFile metadata, LocationExport? cached, CancellationToken ct)
    {
        CurrentKey = $"requestportal/locations/{id:N}/resolved-location.json";
        Operation = "validate-location-metadata";
        if (metadata.StorageKey != CurrentKey || metadata.Status != StorageStatus.Stored || metadata.Length is < 0 or > 32768 ||
            metadata.Metadata is null || metadata.Sha256.Length != 64 || !metadata.Sha256.All(Uri.IsHexDigit)) throw new LocationSourceException("LOCATION_METADATA_INVALID");
        if (metadata.Metadata.TryGetValue("RequestId", out var owner) && (!Guid.TryParse(owner, out var declared) || declared != id))
            throw new LocationSourceException("LOCATION_METADATA_IDENTITY_MISMATCH");
        ExpectedSha256 = metadata.Sha256;
        ExpectedLength = metadata.Length;
        string etag = content.GetProperty("eTag").GetString() ?? "";
        var previous = cached is not null && cached.DriveId == drive && cached.RootFolder == options.RootFolder
            ? cached.Records.SingleOrDefault(x => x.Location.StorageKey == CurrentKey && x.ContentETag == etag && etag.Length > 0 && x.Location.ExpectedSha256 == metadata.Sha256) : null;
        Operation = "read-location-content";
        byte[] payload = previous?.Location.Payload ?? await ContentAsync(ItemUrl(content.GetProperty("id").GetString()!) + "/content", ct);
        Operation = "verify-location-sha256";
        string actual = Convert.ToHexStringLower(SHA256.HashData(payload));
        ActualSha256 = actual;
        ActualLength = payload.Length;
        if (!actual.Equals(metadata.Sha256, StringComparison.OrdinalIgnoreCase) || payload.Length != metadata.Length)
        {
            using var currentItem = await graph.JsonAsync(ItemUrl(content.GetProperty("id").GetString()!) + "?$select=eTag", ct);
            var latest = await MetadataAsync(CurrentKey, ct);
            if (currentItem.RootElement.GetProperty("eTag").GetString() != etag || latest is null || latest.Sha256 != metadata.Sha256 || latest.Version != metadata.Version)
                throw new LocationSourceException("LOCATION_CHANGED_DURING_EXPORT");
            throw new LocationSourceException(!actual.Equals(metadata.Sha256, StringComparison.OrdinalIgnoreCase) ? "STORAGE-CORRUPT-001" : "LOCATION_METADATA_LENGTH_MISMATCH");
        }
        var snapshot = JsonSerializer.Deserialize<RequestLocationSnapshot>(payload, Json);
        if (snapshot is null || snapshot.RequestId != id) throw new LocationSourceException("LOCATION_PAYLOAD_IDENTITY_MISMATCH");
        new PersistedRequestLocation().Apply(snapshot);
        return new(new(CurrentKey, metadata.Sha256, payload), etag);
    }
    public async Task<LocationExport> ReadAsync(IReadOnlySet<Guid> knownRequests, bool writersStopped, LocationExport? cached, CancellationToken ct, Func<Guid, CancellationToken, Task<bool>>? requestExists = null)
    {
        var scope = knownRequests.ToHashSet();
        CurrentKey = null; ExpectedSha256 = null; ActualSha256 = null; ExpectedLength = null; ActualLength = null; Operation = "resolve-drive";
        await DriveAsync(ct);
        Operation = "verify-application-folder";
        // A wrong or inaccessible application root must never be mistaken for an empty geography history.
        if (options.RootFolder.Length > 0)
        {
            using var appRoot = await graph.JsonAsync(PathUrl("") + "?$select=id,folder", ct);
            if (!appRoot.RootElement.TryGetProperty("folder", out _)) throw new LocationSourceException("APPLICATION_ROOT_NOT_FOLDER");
        }
        Operation = "list-location-folders";
        List<JsonElement> folders;
        try { folders = await graph.PagesAsync(PathUrl("requestportal/locations") + ":/children?$select=id,name,folder", ct); }
        catch (LocationSourceException error) when (error.Code == "GRAPH_HTTP_404") { folders = []; }
        var records = new List<LocationSourceRecord>();
        var represented = new HashSet<Guid>(); var missing = new HashSet<Guid>();
        foreach (var folder in folders)
        {
            if (!folder.TryGetProperty("folder", out _) || !Guid.TryParseExact(folder.GetProperty("name").GetString(), "N", out var id))
                throw new LocationSourceException("UNEXPECTED_LOCATION_DIRECTORY");
            CurrentKey = $"requestportal/locations/{id:N}/resolved-location.json";
            ExpectedSha256 = null; ActualSha256 = null; ExpectedLength = null; ActualLength = null;
            if (!scope.Contains(id))
            {
                if (requestExists is null || !await requestExists(id, ct)) throw new LocationSourceException("ORPHAN_LOCATION_REQUIRES_REVIEW");
                scope.Add(id);
            }
            if (!represented.Add(id)) throw new LocationSourceException("DUPLICATE_LOCATION_DIRECTORY");
            Operation = "lookup-location-content";
            var children = await graph.PagesAsync(ItemUrl(folder.GetProperty("id").GetString()!) + "/children?$select=id,name,eTag,size,file", ct);
            var content = children.Where(x => x.GetProperty("name").GetString() == "resolved-location.json").ToArray();
            if (content.Length > 1) throw new LocationSourceException("DUPLICATE_LOCATION_CONTENT");
            Operation = "read-location-metadata";
            StoredFile? metadata = await MetadataAsync(CurrentKey, ct);
            if (metadata is null && content.Length == 0)
            {
                if (cached?.Records.Any(x => x.Location.StorageKey == CurrentKey) == true) throw new LocationSourceException("LOCATION_HISTORY_DISAPPEARED");
                missing.Add(id); continue;
            }
            if (metadata is null) throw new LocationSourceException("LOCATION_METADATA_INVALID");
            if (content.Length == 0) throw new LocationSourceException("LOCATION_CONTENT_MISSING");
            records.Add(await ReadRecordAsync(id, content[0], metadata, cached, ct));
        }
        // A missing content directory does not prove missing metadata. Check every
        // known request's exact location metadata key, never the document metadata index.
        var cachedMissing = writersStopped && cached is { Complete: true, WritersStopped: false } &&
            cached.DriveId == drive && cached.RootFolder == options.RootFolder && cached.ObservedUtc >= DateTimeOffset.UtcNow.AddHours(-2)
            ? cached.VerifiedMissingRequestIds?.ToHashSet() ?? [] : [];
        foreach (Guid id in scope.Where(x => !represented.Contains(x)).Order())
        {
            CurrentKey = $"requestportal/locations/{id:N}/resolved-location.json";
            ExpectedSha256 = null; ActualSha256 = null; ExpectedLength = null; ActualLength = null;
            Operation = "verify-missing-location-metadata";
            // Only the final, stopped-writer pass can reuse prior absence proofs.
            // Common.Storage creates content before metadata. All new/changed content
            // was enumerated above; the root recovery command also fences the old PID.
            if (cachedMissing.Contains(id)) { missing.Add(id); continue; }
            var metadata = await MetadataAsync(CurrentKey, ct);
            if (metadata is not null)
            {
                Operation = "lookup-location-content";
                try
                {
                    using var content = await graph.JsonAsync(PathUrl(CurrentKey) + "?$select=id,eTag,size,file", ct);
                    records.Add(await ReadRecordAsync(id, content.RootElement, metadata, cached, ct));
                }
                catch (LocationSourceException error) when (error.Code == "GRAPH_HTTP_404") { throw new LocationSourceException("LOCATION_CONTENT_MISSING"); }
                continue;
            }
            if (cached?.Records.Any(x => x.Location.StorageKey == CurrentKey) == true) throw new LocationSourceException("LOCATION_HISTORY_DISAPPEARED");
            missing.Add(id);
        }
        CurrentKey = null; Operation = "location-export-complete";
        var result = new LocationExport(true, writersStopped, drive!, options.RootFolder, DateTimeOffset.UtcNow, records, missing.Order().ToArray(), scope.Count);
        LocationExportValidation.ValidateCoverage(result);
        return result;
    }
}
