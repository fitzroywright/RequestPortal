using System.Security.Cryptography;
using System.Text.Json;
using Common.Storage;

namespace RequestPortal.StorageMigration;

/// <summary>Imports the pinned Common.Storage filesystem format without rewriting source metadata or versions.</summary>
public sealed class NetworkFolderImporter(string root)
{
    public static string MetadataFingerprint(StoredFile value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        value with {Metadata=value.Metadata.OrderBy(x=>x.Key,StringComparer.Ordinal).ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal)})));
    public static void ValidateMetadata(StoredFile value)
    {
        ValidateKey(value.StorageKey);
        if(value.Version<1 || value.Length<0 || value.Metadata is null || string.IsNullOrEmpty(value.Sha256) || value.Sha256.Length!=64 || !value.Sha256.All(Uri.IsHexDigit) || value.Status!=StorageStatus.Stored)
            throw new MigrationSourceException("INVALID_SOURCE_METADATA");
    }
    public static void ValidateKey(string key)
    {
        if(string.IsNullOrWhiteSpace(key) || key!=key.Replace('\\','/').Trim('/') || Path.IsPathRooted(key) ||
           key.Split('/').Any(x=>x is "" or "." or ".." || x.Any(c=>char.IsControl(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|')))
            throw new MigrationSourceException("UNSAFE_STORAGE_KEY");
    }
    private string Resolve(string key)
    {
        ValidateKey(key);
        string fullRoot=Path.GetFullPath(root);string path=Path.GetFullPath(Path.Combine(fullRoot,key));
        if(!path.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.Ordinal))throw new MigrationSourceException("DESTINATION_PATH_GUARD");
        for(string? current=path;current is not null;current=Path.GetDirectoryName(current))
        {
            if((File.Exists(current)||Directory.Exists(current)) && (File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new MigrationSourceException("DESTINATION_LINK_GUARD");
            if(current==fullRoot)break;
        }
        return path;
    }
    public static void ValidateInventory(IEnumerable<StoredFile> files)
    {
        var occupied=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in files)
        {
            ValidateMetadata(file);
            foreach(string path in new[] {file.StorageKey,file.StorageKey+".json",file.StorageKey+".versions"})
                if(!occupied.Add(path))throw new MigrationSourceException("FILESYSTEM_FORMAT_COLLISION");
        }
        foreach(string path in occupied)
            for(int i=path.IndexOf('/');i>=0;i=path.IndexOf('/',i+1))
                if(occupied.Contains(path[..i]))throw new MigrationSourceException("FILESYSTEM_PREFIX_COLLISION");
    }
    public static async Task<string> HashAsync(string path,CancellationToken ct)
    {
        await using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,131072,FileOptions.Asynchronous|FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file,ct));
    }
    public async Task ImportVersionAsync(StoredFile metadata,string verifiedContent,CancellationToken ct)
    {
        ValidateMetadata(metadata);
        await VerifyContentAsync(verifiedContent,metadata,ct);
        string directory=Resolve(metadata.StorageKey)+".versions";
        Directory.CreateDirectory(directory);
        // Recheck after creation and before publication; no source file ever overwrites an existing target.
        Resolve(metadata.StorageKey+".versions/"+metadata.Version.ToString("D8")+".bin");
        string version=Path.Combine(directory,metadata.Version.ToString("D8"));
        await PublishBlobAsync(verifiedContent,version+".bin",metadata,ct);
        Resolve(metadata.StorageKey+".versions/"+metadata.Version.ToString("D8")+".json");
        await PublishMetadataAsync(version+".json",metadata,ct);
        var target=new NetworkFolderStorage(Path.GetFullPath(root));
        var actual=(await target.GetVersionsAsync(metadata.StorageKey,ct)).Single(x=>x.Version==metadata.Version);
        if(MetadataFingerprint(actual)!=MetadataFingerprint(metadata))throw new MigrationSourceException("DESTINATION_METADATA_MISMATCH");
    }
    public async Task PublishCurrentAsync(StoredFile metadata,string verifiedContent,CancellationToken ct,StoredFile? previousImported=null)
    {
        ValidateMetadata(metadata);string path=Resolve(metadata.StorageKey);
        await VerifyContentAsync(verifiedContent,metadata,ct);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);Resolve(metadata.StorageKey);
        Resolve(metadata.StorageKey+".json");
        if(File.Exists(path+".json"))
        {
            var existing=JsonSerializer.Deserialize<StoredFile>(await File.ReadAllTextAsync(path+".json",ct));
            bool already=existing is not null && MetadataFingerprint(existing)==MetadataFingerprint(metadata);
            if(!already && (existing is null || previousImported is null || MetadataFingerprint(existing)!=MetadataFingerprint(previousImported)))
                throw new MigrationSourceException("DESTINATION_METADATA_CONFLICT");
            if(!already)
            {
                // Resume may find the new bytes with the previous metadata after an interrupted publication.
                string actualBlobHash=File.Exists(path)?await HashAsync(path,ct):"";
                if(actualBlobHash!=metadata.Sha256.ToLowerInvariant() && actualBlobHash!=previousImported!.Sha256.ToLowerInvariant())
                    throw new MigrationSourceException("DESTINATION_MODIFIED_SINCE_IMPORT");
                await ReplaceOwnedAsync(verifiedContent,path,metadata,ct);
                await ReplaceMetadataAsync(path+".json",metadata,ct);
            }
        }
        await PublishBlobAsync(verifiedContent,path,metadata,ct);
        await PublishMetadataAsync(path+".json",metadata,ct);
        var target=new NetworkFolderStorage(Path.GetFullPath(root));
        var actual=await target.GetMetadataAsync(metadata.StorageKey,ct) ?? throw new MigrationSourceException("DESTINATION_METADATA_MISSING");
        if(MetadataFingerprint(actual)!=MetadataFingerprint(metadata))throw new MigrationSourceException("DESTINATION_METADATA_MISMATCH");
        await using var read=await target.OpenReadAsync(metadata.StorageKey,ct);
        if(!string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(read,ct)),metadata.Sha256,StringComparison.OrdinalIgnoreCase))
            throw new MigrationSourceException("DESTINATION_HASH_MISMATCH");
    }
    private static async Task ReplaceOwnedAsync(string source,string target,StoredFile metadata,CancellationToken ct)
    {
        string candidate=target+"."+Guid.NewGuid().ToString("N")+".migrating";
        try {File.Copy(source,candidate,false);await VerifyContentAsync(candidate,metadata,ct);File.Move(candidate,target,true);}
        finally {if(File.Exists(candidate))File.Delete(candidate);}
    }
    private static async Task ReplaceMetadataAsync(string target,StoredFile metadata,CancellationToken ct)
    {
        string candidate=target+"."+Guid.NewGuid().ToString("N")+".migrating";
        try {await File.WriteAllTextAsync(candidate,JsonSerializer.Serialize(metadata),ct);File.Move(candidate,target,true);}
        finally {if(File.Exists(candidate))File.Delete(candidate);}
    }
    private static async Task VerifyContentAsync(string content,StoredFile metadata,CancellationToken ct,string errorCode="SOURCE_SHA256_MISMATCH")
    {
        if(new FileInfo(content).Length!=metadata.Length || !string.Equals(await HashAsync(content,ct),metadata.Sha256,StringComparison.OrdinalIgnoreCase))
            throw new MigrationSourceException(errorCode);
    }
    private static async Task PublishBlobAsync(string source,string target,StoredFile metadata,CancellationToken ct)
    {
        if(File.Exists(target)) {await VerifyContentAsync(target,metadata,ct,"DESTINATION_HASH_CONFLICT");return;}
        string candidate=target+"."+Guid.NewGuid().ToString("N")+".migrating";
        try
        {
            File.Copy(source,candidate,false);await VerifyContentAsync(candidate,metadata,ct);
            File.Move(candidate,target,false);
        }
        finally {if(File.Exists(candidate))File.Delete(candidate);}
    }
    private static async Task PublishMetadataAsync(string target,StoredFile metadata,CancellationToken ct)
    {
        if(File.Exists(target))
        {
            var existing=JsonSerializer.Deserialize<StoredFile>(await File.ReadAllTextAsync(target,ct));
            if(existing is null || MetadataFingerprint(existing)!=MetadataFingerprint(metadata))throw new MigrationSourceException("DESTINATION_METADATA_CONFLICT");
            return;
        }
        string candidate=target+"."+Guid.NewGuid().ToString("N")+".migrating";
        try {await File.WriteAllTextAsync(candidate,JsonSerializer.Serialize(metadata),ct);File.Move(candidate,target,false);}
        finally {if(File.Exists(candidate))File.Delete(candidate);}
    }
}
