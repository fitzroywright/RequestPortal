using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Storage;
using RequestPortal.Application.Management;
using RequestPortal.Infrastructure.Management;

namespace RequestPortal.LocationRecovery;

public sealed record LocationSourceRecord(LegacyRequestLocation Location, string ContentETag);
public sealed record LocationExport(bool Complete, bool WritersStopped, string DriveId, string RootFolder,
    DateTimeOffset ObservedUtc, IReadOnlyList<LocationSourceRecord> Records);

/// <summary>Reads only requestportal/locations and the metadata of those exact keys. No document inventory.</summary>
public sealed class SharePointLocationExport(GraphReadClient graph, SharePointStorageOptions options)
{
    private const string Graph = "https://graph.microsoft.com/v1.0";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Operation { get; private set; } = "resolve-drive";
    public string? CurrentKey { get; private set; }
    public string? ExpectedSha256 { get; private set; }
    public string? ActualSha256 { get; private set; }
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
        return drive = drives.Single(x => x.GetProperty("name").GetString() == options.DriveName).GetProperty("id").GetString()!;
    }
    private string PathUrl(string relative) => $"{Graph}/drives/{Uri.EscapeDataString(drive!)}/root:/{Encode(string.Join('/', new[] { options.RootFolder.Trim('/'), relative }.Where(x => x.Length > 0)))}";
    private string ItemUrl(string id) => $"{Graph}/drives/{Uri.EscapeDataString(drive!)}/items/{Uri.EscapeDataString(id)}";
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
    public async Task<LocationExport> ReadAsync(IReadOnlySet<Guid> knownRequests, bool writersStopped, LocationExport? cached, CancellationToken ct)
    {
        CurrentKey = null; ExpectedSha256 = null; ActualSha256 = null; Operation = "resolve-drive";
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
        foreach (var folder in folders)
        {
            if (!folder.TryGetProperty("folder", out _) || !Guid.TryParseExact(folder.GetProperty("name").GetString(), "N", out var id))
                throw new LocationSourceException("UNEXPECTED_LOCATION_DIRECTORY");
            CurrentKey = $"requestportal/locations/{id:N}/resolved-location.json";
            ExpectedSha256 = null; ActualSha256 = null;
            if (!knownRequests.Contains(id)) throw new LocationSourceException("ORPHAN_LOCATION_REQUIRES_REVIEW");
            Operation = "lookup-location-content";
            var children = await graph.PagesAsync(ItemUrl(folder.GetProperty("id").GetString()!) + "/children?$select=id,name,eTag,size,file", ct);
            var content = children.Where(x => x.GetProperty("name").GetString() == "resolved-location.json").ToArray();
            if (content.Length > 1) throw new LocationSourceException("DUPLICATE_LOCATION_CONTENT");
            Operation = "read-location-metadata";
            string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CurrentKey)));
            StoredFile? metadata;
            try
            {
                using var document = await graph.JsonAsync(PathUrl(".common-storage/metadata/" + digest + ".json") + ":/content", ct);
                metadata = document.RootElement.Deserialize<StoredFile>(Json);
            }
            catch (LocationSourceException error) when (error.Code == "GRAPH_HTTP_404" && content.Length == 0) { continue; }
            if (metadata is null || metadata.StorageKey != CurrentKey || metadata.Status != StorageStatus.Stored || metadata.Length is < 0 or > 32768 ||
                metadata.Metadata is null || metadata.Sha256.Length != 64 || !metadata.Sha256.All(Uri.IsHexDigit)) throw new LocationSourceException("LOCATION_METADATA_INVALID");
            if (metadata.Metadata.TryGetValue("RequestId", out var owner) && (!Guid.TryParse(owner, out var declared) || declared != id))
                throw new LocationSourceException("LOCATION_METADATA_IDENTITY_MISMATCH");
            if (content.Length == 0) throw new LocationSourceException("LOCATION_CONTENT_MISSING");
            ExpectedSha256 = metadata.Sha256;
            string etag = content[0].GetProperty("eTag").GetString() ?? "";
            var previous = cached is not null && cached.DriveId == drive && cached.RootFolder == options.RootFolder
                ? cached.Records.SingleOrDefault(x => x.Location.StorageKey == CurrentKey && x.ContentETag == etag && etag.Length > 0 && x.Location.ExpectedSha256 == metadata.Sha256) : null;
            Operation = "read-location-content";
            byte[] payload = previous?.Location.Payload ?? await ContentAsync(ItemUrl(content[0].GetProperty("id").GetString()!) + "/content", ct);
            Operation = "verify-location-sha256";
            string actual = Convert.ToHexStringLower(SHA256.HashData(payload));
            ActualSha256 = actual;
            if (!actual.Equals(metadata.Sha256, StringComparison.OrdinalIgnoreCase) || payload.Length != metadata.Length)
            {
                using var currentItem = await graph.JsonAsync(ItemUrl(content[0].GetProperty("id").GetString()!) + "?$select=eTag", ct);
                using var currentMetadata = await graph.JsonAsync(PathUrl(".common-storage/metadata/" + digest + ".json") + ":/content", ct);
                var latest = currentMetadata.RootElement.Deserialize<StoredFile>(Json);
                if (currentItem.RootElement.GetProperty("eTag").GetString() != etag || latest is null || latest.Sha256 != metadata.Sha256 || latest.Version != metadata.Version)
                    throw new LocationSourceException("LOCATION_CHANGED_DURING_EXPORT");
                throw new LocationSourceException("STORAGE-CORRUPT-001");
            }
            var snapshot = JsonSerializer.Deserialize<RequestLocationSnapshot>(payload, Json);
            if (snapshot is null || snapshot.RequestId != id) throw new LocationSourceException("LOCATION_PAYLOAD_IDENTITY_MISMATCH");
            new PersistedRequestLocation().Apply(snapshot);
            records.Add(new(new(CurrentKey, metadata.Sha256, payload), etag));
        }
        CurrentKey = null; Operation = "location-export-complete";
        return new(true, writersStopped, drive!, options.RootFolder, DateTimeOffset.UtcNow, records);
    }
}
