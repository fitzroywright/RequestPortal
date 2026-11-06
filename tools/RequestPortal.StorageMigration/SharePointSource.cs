using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Storage;

namespace RequestPortal.StorageMigration;

public sealed record SourceNode(string Id,string Path,string ETag,long Size,bool Folder);
public sealed record NativeSourceVersion(string Id,long Size,string MetadataJson);
public sealed record SourceFile(StoredFile Current,string MetadataItemId,string MetadataETag,SourceNode? Content,IReadOnlyList<StoredFile> Versions);
public sealed record SourceInventory(string SourceFingerprint,DateTimeOffset ObservedUtc,string DriveId,string RootFolder,
    IReadOnlyList<SourceNode> Nodes,IReadOnlyList<SourceFile> Files,IReadOnlyList<string> Problems,bool Provisional);

/// <summary>Scopes every read to the configured Request Portal folder. No source mutation API exists.</summary>
public sealed class SharePointSource(GraphReadClient graph,SharePointStorageOptions options)
{
    private const string Graph="https://graph.microsoft.com/v1.0";
    private string? drive;
    public async Task<string> DriveAsync(CancellationToken ct)
    {
        if(drive is not null)return drive;
        if(!string.IsNullOrWhiteSpace(options.DriveId))return drive=options.DriveId;
        string? site=options.SiteId;
        if(string.IsNullOrWhiteSpace(site))
        {
            if(string.IsNullOrWhiteSpace(options.HostName))throw new MigrationSourceException("SOURCE_SITE_MISSING");
            using var response=await graph.JsonAsync($"{Graph}/sites/{Uri.EscapeDataString(options.HostName)}:/{Encode(options.SitePath)}?$select=id",ct);
            site=response.RootElement.GetProperty("id").GetString();
        }
        if(string.IsNullOrWhiteSpace(options.DriveName))
        {
            using var response=await graph.JsonAsync($"{Graph}/sites/{Uri.EscapeDataString(site!)}/drive?$select=id",ct);
            return drive=response.RootElement.GetProperty("id").GetString()!;
        }
        var drives=await graph.PagesAsync($"{Graph}/sites/{Uri.EscapeDataString(site!)}/drives?$select=id,name",ct);
        return drive=drives.Single(x=>x.GetProperty("name").GetString()==options.DriveName).GetProperty("id").GetString()!;
    }
    private static string Encode(string path)=>string.Join('/',path.Trim('/').Split('/').Select(Uri.EscapeDataString));
    private async Task<string> ItemUrl(string relative,CancellationToken ct)=>$"{Graph}/drives/{Uri.EscapeDataString(await DriveAsync(ct))}/root:/{Encode(options.RootFolder)}/{Encode(relative)}";
    private async Task<string> IdUrl(string id,CancellationToken ct)=>$"{Graph}/drives/{Uri.EscapeDataString(await DriveAsync(ct))}/items/{Uri.EscapeDataString(id)}";
    public async Task<StoredFile> MetadataAsync(string id,CancellationToken ct)
    {
        using var response=await graph.JsonAsync(await IdUrl(id,ct)+"/content",ct);
        var value=response.RootElement.Deserialize<StoredFile>(new JsonSerializerOptions {PropertyNameCaseInsensitive=true})
            ?? throw new MigrationSourceException("SOURCE_METADATA_EMPTY");
        NetworkFolderImporter.ValidateMetadata(value);return value;
    }
    public async Task<SourceInventory> InventoryAsync(CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(options.RootFolder))throw new MigrationSourceException("SOURCE_ROOT_REQUIRED");
        using var root=await graph.JsonAsync($"{Graph}/drives/{Uri.EscapeDataString(await DriveAsync(ct))}/root:/{Encode(options.RootFolder)}?$select=id",ct);
        var queue=new Queue<(string Id,string Path)>();queue.Enqueue((root.RootElement.GetProperty("id").GetString()!,""));
        var nodes=new List<SourceNode>();var seen=new HashSet<string>(StringComparer.Ordinal);var problems=new List<string>();
        while(queue.TryDequeue(out var folder))
        {
            if(!seen.Add(folder.Id))throw new MigrationSourceException("SOURCE_FOLDER_CYCLE");
            foreach(var item in await graph.PagesAsync(await IdUrl(folder.Id,ct)+"/children?$select=id,name,eTag,size,folder,file",ct))
            {
                string name=item.GetProperty("name").GetString()!;
                if(name.Contains('/') || name.Contains('\\') || name is "." or "..")throw new MigrationSourceException("SOURCE_PATH_INVALID");
                string path=folder.Path.Length==0 ? name : folder.Path+"/"+name;
                bool directory=item.TryGetProperty("folder",out _);
                var node=new SourceNode(item.GetProperty("id").GetString()!,path,
                    item.TryGetProperty("eTag",out var etag)?etag.GetString()??"":"",item.TryGetProperty("size",out var size)?size.GetInt64():0,directory);
                nodes.Add(node);if(directory)queue.Enqueue((node.Id,path));
            }
        }
        var paths=nodes.ToDictionary(x=>x.Path,StringComparer.OrdinalIgnoreCase);
        var files=new List<SourceFile>();
        foreach(var node in nodes.Where(x=>!x.Folder && x.Path.StartsWith(".common-storage/metadata/",StringComparison.Ordinal)))
        {
            try
            {
                var metadata=await MetadataAsync(node.Id,ct);
                if(metadata.Metadata.TryGetValue("Application",out var owner) && !owner.Equals("RequestPortal",StringComparison.OrdinalIgnoreCase))
                    throw new MigrationSourceException("FOREIGN_APPLICATION_METADATA");
                string digest=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(metadata.StorageKey)));
                if(node.Path!=".common-storage/metadata/"+digest+".json")throw new MigrationSourceException("SOURCE_METADATA_KEY_MISMATCH");
                var versions=new List<StoredFile>();
                foreach(var versionNode in nodes.Where(x=>!x.Folder && x.Path.StartsWith(".common-storage/versions/"+digest+"/",StringComparison.Ordinal)))
                {
                    var version=await MetadataAsync(versionNode.Id,ct);
                    if(version.StorageKey!=metadata.StorageKey || versionNode.Path!=".common-storage/versions/"+digest+"/"+version.Version.ToString("D8")+".json")
                        throw new MigrationSourceException("SOURCE_VERSION_METADATA_MISMATCH");
                    versions.Add(version);
                }
                if(versions.All(x=>x.Version!=metadata.Version))versions.Add(metadata);
                if(versions.Where(x=>x.Version==metadata.Version).Any(x=>x.Length!=metadata.Length || !x.Sha256.Equals(metadata.Sha256,StringComparison.OrdinalIgnoreCase)))
                    throw new MigrationSourceException("SOURCE_CURRENT_VERSION_CONFLICT");
                paths.TryGetValue(metadata.StorageKey,out var content);
                if(content is null || content.Folder)
                {
                    problems.Add("SOURCE_CONTENT_MISSING:"+node.Id);content=null;
                }
                files.Add(new(metadata,node.Id,node.ETag,content,versions.OrderBy(x=>x.Version).ToArray()));
            }
            catch(MigrationSourceException error) {problems.Add(error.Code+":"+node.Id);}
        }
        NetworkFolderImporter.ValidateInventory(files.Select(x=>x.Current));
        string fingerprint=Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
            drive=await DriveAsync(ct),root=options.RootFolder,nodes=nodes.OrderBy(x=>x.Path,StringComparer.Ordinal).ToArray(),
            files=files.OrderBy(x=>x.Current.StorageKey,StringComparer.Ordinal).ToArray()})));
        // Live traversal is deliberately provisional. Final cutover requires stopped writers and two identical complete passes.
        return new(fingerprint,DateTimeOffset.UtcNow,await DriveAsync(ct),options.RootFolder,nodes,files,problems,true);
    }
    public async Task<string> NodeETagAsync(string id,CancellationToken ct)
    {
        using var item=await graph.JsonAsync(await IdUrl(id,ct)+"?$select=eTag",ct);
        return item.RootElement.GetProperty("eTag").GetString()??"";
    }
    public async Task<string> DownloadAsync(string itemId,string? versionId,string destination,long maximum,CancellationToken ct)
    {
        string url=await IdUrl(itemId,ct)+(versionId is null?"/content":"/versions/"+Uri.EscapeDataString(versionId)+"/content");
        using var response=await graph.SendAsync(url,ct);
        await using var input=await response.Content.ReadAsStreamAsync(ct);
        await using var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None,131072,true);
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(destination,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);byte[] buffer=new byte[131072];long bytes=0;
        while(true)
        {
            int n=await input.ReadAsync(buffer,ct);if(n==0)break;bytes+=n;
            if(bytes>maximum)throw new MigrationSourceException("SOURCE_LENGTH_MISMATCH");
            hash.AppendData(buffer,0,n);await output.WriteAsync(buffer.AsMemory(0,n),ct);
        }
        await output.FlushAsync(ct);return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    public async Task<IReadOnlyList<NativeSourceVersion>> NativeVersionsAsync(string itemId,CancellationToken ct)
    {
        var versions=await graph.PagesAsync(await IdUrl(itemId,ct)+"/versions?$select=id,size,lastModifiedDateTime,lastModifiedBy",ct);
        return versions.Select(x=>new NativeSourceVersion(x.GetProperty("id").GetString()!,x.TryGetProperty("size",out var size)?size.GetInt64():-1,x.GetRawText())).ToArray();
    }
    public async Task<bool> MetadataUnchangedAsync(SourceFile file,CancellationToken ct)=>
        NetworkFolderImporter.MetadataFingerprint(await MetadataAsync(file.MetadataItemId,ct))==NetworkFolderImporter.MetadataFingerprint(file.Current);
}
