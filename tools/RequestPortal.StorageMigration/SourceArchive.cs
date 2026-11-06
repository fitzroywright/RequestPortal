using System.Text.Json;

namespace RequestPortal.StorageMigration;

public sealed record ArchiveEntry(string SourcePath,string SourceItemId,string SourceETag,string Sha256,long Length,string Kind);
public sealed record NativeVersionArchive(string SourceItemId,string StorageKey,string NativeVersionId,string MetadataJson,string SourceContentETag,string Sha256,long Length,string? ManagedKey,int? ManagedVersion);
public sealed record ArchiveEvidence(string SourceFingerprint,int ArchivedControlFiles,int ArchivedUnmanagedFiles,
    IReadOnlyList<ArchiveEntry> Entries,IReadOnlyList<NativeVersionArchive>? NativeVersions=null);

/// <summary>Preserves provider sidecars and unreferenced raw objects outside the active Common.Storage root.</summary>
public static class SourceArchive
{
    private static string SafePath(string root,string relative)
    {
        NetworkFolderImporter.ValidateKey(relative);
        string fullRoot=Path.GetFullPath(root),path=Path.GetFullPath(Path.Combine(fullRoot,relative));
        if(!path.StartsWith(fullRoot.TrimEnd('/')+"/",StringComparison.Ordinal))throw new MigrationSourceException("ARCHIVE_PATH_GUARD");
        for(string? part=path;part is not null;part=Path.GetDirectoryName(part))
        {
            if((File.Exists(part)||Directory.Exists(part)) && (File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)
                throw new MigrationSourceException("ARCHIVE_LINK_GUARD");
            if(part==fullRoot)break;
        }
        return path;
    }
    private static string BlobPath(string root,string sha256)
    {
        if(sha256.Length!=64 || !sha256.All(Uri.IsHexDigit))throw new MigrationSourceException("ARCHIVE_HASH_INVALID");
        return SafePath(root,"objects/"+sha256+".bin");
    }
    public static async Task<ArchiveEvidence> CopyAsync(SharePointSource source,SourceInventory inventory,string archiveRoot,string state,string destination,CancellationToken ct)
    {
        SafePath(archiveRoot,"objects");
        Directory.CreateDirectory(archiveRoot);Directory.CreateDirectory(Path.Combine(archiveRoot,"objects"));
        var managed=inventory.Files.Select(x=>x.Current.StorageKey).ToHashSet(StringComparer.Ordinal);
        var entries=new List<ArchiveEntry>();
        string checkpoint=SafePath(archiveRoot,"source-archive-"+inventory.SourceFingerprint+".json");
        Dictionary<string,ArchiveEntry> previous=[];
        if(File.Exists(checkpoint))
        {
            var saved=JsonSerializer.Deserialize<ArchiveEvidence>(await File.ReadAllTextAsync(checkpoint,ct))
                ?? throw new MigrationSourceException("SOURCE_ARCHIVE_MANIFEST_INVALID");
            previous=saved.Entries.ToDictionary(x=>x.SourceItemId,StringComparer.Ordinal);
        }
        foreach(var node in inventory.Nodes.Where(x=>!x.Folder && !managed.Contains(x.Path)))
        {
            bool control=node.Path.StartsWith(".common-storage/",StringComparison.Ordinal);
            if(previous.TryGetValue(node.Id,out var old) && old.SourceETag==node.ETag)
            {
                string blob=BlobPath(archiveRoot,old.Sha256);
                if(File.Exists(blob) && new FileInfo(blob).Length==old.Length && await NetworkFolderImporter.HashAsync(blob,ct)==old.Sha256)
                {entries.Add(old);continue;}
            }
            string temp=Path.Combine(state,"archive-"+Guid.NewGuid().ToString("N")+".bin");
            try
            {
                if(await source.NodeETagAsync(node.Id,ct)!=node.ETag)throw new MigrationSourceException("SOURCE_CHANGED_DURING_ARCHIVE");
                string hash=await source.DownloadAsync(node.Id,null,temp,node.Size,ct);
                if(new FileInfo(temp).Length!=node.Size)throw new MigrationSourceException("SOURCE_LENGTH_MISMATCH");
                if(await source.NodeETagAsync(node.Id,ct)!=node.ETag)throw new MigrationSourceException("SOURCE_CHANGED_DURING_ARCHIVE");
                string blob=BlobPath(archiveRoot,hash);
                if(!File.Exists(blob))File.Copy(temp,blob,false);
                if(await NetworkFolderImporter.HashAsync(blob,ct)!=hash)throw new MigrationSourceException("ARCHIVE_SHA256_MISMATCH");
                entries.Add(new(node.Path,node.Id,node.ETag,hash,new FileInfo(temp).Length,control?"ProviderMetadata":"UnmanagedSourceFile"));
                var interim=new ArchiveEvidence(inventory.SourceFingerprint,entries.Count(x=>x.Kind=="ProviderMetadata"),entries.Count(x=>x.Kind=="UnmanagedSourceFile"),entries);
                await SaveAsync(checkpoint,interim,ct);
            }
            finally {if(File.Exists(temp))File.Delete(temp);}
        }
        var native=await CopyNativeHistoryAsync(source,inventory,archiveRoot,state,destination,ct);
        var evidence=new ArchiveEvidence(inventory.SourceFingerprint,entries.Count(x=>x.Kind=="ProviderMetadata"),entries.Count(x=>x.Kind=="UnmanagedSourceFile"),entries,native);
        await SaveAsync(checkpoint,evidence,ct);return evidence;
    }
    private static async Task<IReadOnlyList<NativeVersionArchive>> CopyNativeHistoryAsync(SharePointSource source,SourceInventory inventory,
        string archiveRoot,string state,string destination,CancellationToken ct)
    {
        string indexPath=SafePath(archiveRoot,"native-version-index.json");
        SafePath(archiveRoot,"native-version-index.json.writing");
        var index=File.Exists(indexPath)?JsonSerializer.Deserialize<List<NativeVersionArchive>>(await File.ReadAllTextAsync(indexPath,ct))??[]:[];
        var result=new List<NativeVersionArchive>();
        foreach(var file in inventory.Files.Where(x=>x.Content is not null))
        {
            var content=file.Content!;
            if(await source.NodeETagAsync(content.Id,ct)!=content.ETag)throw new MigrationSourceException("SOURCE_CHANGED_DURING_NATIVE_ARCHIVE");
            var versions=await source.NativeVersionsAsync(content.Id,ct);
            for(int i=0;i<versions.Count;i++)
            {
                var version=versions[i];
                var cached=index.SingleOrDefault(x=>x.SourceItemId==content.Id && x.NativeVersionId==version.Id && x.MetadataJson==version.MetadataJson && (i!=0 || x.SourceContentETag==content.ETag));
                if(cached is not null && await VerifyNativeAsync(cached,archiveRoot,destination,ct))
                {
                    // Historical bytes stay immutable; re-fence their association to the freshly observed content item.
                    result.Add(cached with {SourceContentETag=content.ETag});continue;
                }
                string temp=Path.Combine(state,"native-"+Guid.NewGuid().ToString("N")+".bin");
                try
                {
                    string hash=await source.DownloadAsync(content.Id,i==0?null:version.Id,temp,version.Size>=0?version.Size:content.Size,ct);
                    if(version.Size>=0 && new FileInfo(temp).Length!=version.Size)throw new MigrationSourceException("SOURCE_LENGTH_MISMATCH");
                    var logical=file.Versions.FirstOrDefault(x=>x.Sha256.Equals(hash,StringComparison.OrdinalIgnoreCase));
                    if(logical is null)
                    {
                        string blob=BlobPath(archiveRoot,hash);
                        if(!File.Exists(blob))File.Copy(temp,blob,false);
                        if(await NetworkFolderImporter.HashAsync(blob,ct)!=hash)throw new MigrationSourceException("NATIVE_ARCHIVE_SHA256_MISMATCH");
                    }
                    var record=new NativeVersionArchive(content.Id,file.Current.StorageKey,version.Id,version.MetadataJson,content.ETag,hash,new FileInfo(temp).Length,logical?.StorageKey,logical?.Version);
                    if(!await VerifyNativeAsync(record,archiveRoot,destination,ct))throw new MigrationSourceException("NATIVE_ARCHIVE_VERIFICATION_FAILED");
                    index.RemoveAll(x=>x.SourceItemId==content.Id && x.NativeVersionId==version.Id);index.Add(record);result.Add(record);
                    string writing=indexPath+".writing";await File.WriteAllTextAsync(writing,JsonSerializer.Serialize(index),ct);File.Move(writing,indexPath,true);
                }
                finally {if(File.Exists(temp))File.Delete(temp);}
            }
            if(await source.NodeETagAsync(content.Id,ct)!=content.ETag)throw new MigrationSourceException("SOURCE_CHANGED_DURING_NATIVE_ARCHIVE");
        }
        return result;
    }
    private static async Task<bool> VerifyNativeAsync(NativeVersionArchive record,string archiveRoot,string destination,CancellationToken ct)
    {
        if(record.Sha256.Length!=64 || !record.Sha256.All(Uri.IsHexDigit))return false;
        if(record.ManagedKey is not null && record.ManagedVersion is { } version)
        {
            NetworkFolderImporter.ValidateKey(record.ManagedKey);
            var target=new global::Common.Storage.NetworkFolderStorage(destination);
            await using var stream=await target.OpenVersionAsync(record.ManagedKey,version,ct);
            return Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream,ct))==record.Sha256;
        }
        string path=BlobPath(archiveRoot,record.Sha256);
        return File.Exists(path) && new FileInfo(path).Length==record.Length && await NetworkFolderImporter.HashAsync(path,ct)==record.Sha256;
    }
    public static async Task<bool> VerifyAsync(SourceInventory inventory,string archiveRoot,string destination,SharePointSource source,CancellationToken ct)
    {
        string checkpoint=SafePath(archiveRoot,"source-archive-"+inventory.SourceFingerprint+".json");
        if(!File.Exists(checkpoint))return false;
        var saved=JsonSerializer.Deserialize<ArchiveEvidence>(await File.ReadAllTextAsync(checkpoint,ct));
        if(saved is null || saved.SourceFingerprint!=inventory.SourceFingerprint)return false;
        var managed=inventory.Files.Select(x=>x.Current.StorageKey).ToHashSet(StringComparer.Ordinal);
        var required=inventory.Nodes.Where(x=>!x.Folder && !managed.Contains(x.Path)).ToArray();
        if(required.Length!=saved.Entries.Count)return false;
        foreach(var item in required)
        {
            var entry=saved.Entries.SingleOrDefault(x=>x.SourceItemId==item.Id && x.SourceETag==item.ETag && x.SourcePath==item.Path);
            if(entry is null || entry.Sha256.Length!=64 || !entry.Sha256.All(Uri.IsHexDigit))return false;
            string blob=BlobPath(archiveRoot,entry.Sha256);
            if(!File.Exists(blob) || (File.GetAttributes(blob)&FileAttributes.ReparsePoint)!=0 ||
               new FileInfo(blob).Length!=entry.Length || await NetworkFolderImporter.HashAsync(blob,ct)!=entry.Sha256)return false;
        }
        if(saved.NativeVersions is null)return false;
        int expectedVersions=0;
        foreach(var file in inventory.Files.Where(x=>x.Content is not null))
        {
            var content=file.Content!;
            if(await source.NodeETagAsync(content.Id,ct)!=content.ETag)return false;
            var versions=await source.NativeVersionsAsync(content.Id,ct);expectedVersions+=versions.Count;
            foreach(var version in versions)
                if(saved.NativeVersions.Count(x=>x.SourceItemId==content.Id && x.StorageKey==file.Current.StorageKey &&
                    x.NativeVersionId==version.Id && x.MetadataJson==version.MetadataJson && x.SourceContentETag==content.ETag)!=1)return false;
            if(await source.NodeETagAsync(content.Id,ct)!=content.ETag)return false;
        }
        if(saved.NativeVersions.Count!=expectedVersions)return false;
        foreach(var record in saved.NativeVersions)
            if(!await VerifyNativeAsync(record,archiveRoot,destination,ct))return false;
        return true;
    }
    private static async Task SaveAsync(string path,ArchiveEvidence value,CancellationToken ct)
    {
        string temp=SafePath(Path.GetDirectoryName(path)!,Path.GetFileName(path)+".writing");
        await File.WriteAllTextAsync(temp,JsonSerializer.Serialize(value),ct);File.Move(temp,path,true);
    }
}
