using Common.Storage;

namespace RequestPortal.StorageMigration;

public sealed class MigrationEngine(SharePointSource source,string state,string destination)
{
    public async Task<MigrationLedger> CopyAsync(SourceInventory inventory,CancellationToken ct)
    {
        if(!Directory.Exists(destination))throw new MigrationSourceException("DESTINATION_NOT_PROVISIONED");
        var importer=new NetworkFolderImporter(destination);var ledger=await MigrationLedger.LoadAsync(state,ct);
        ledger.SourceFingerprint=inventory.SourceFingerprint;ledger.FinalSourceFenceVerified=false;
        ledger.Problems=inventory.Problems.ToList();
        foreach(var item in inventory.Files)
            if(!ledger.Files.ContainsKey(item.Current.StorageKey))ledger.Files[item.Current.StorageKey]=new(item.Current,"Pending",DateTimeOffset.UtcNow,0,false,null,false,null);
        await ledger.SaveAsync(state,ct);
        var target=new NetworkFolderStorage(destination);
        foreach(var file in inventory.Files)
        {
            ct.ThrowIfCancellationRequested();string key=file.Current.StorageKey;
            ledger.Files.TryGetValue(key,out var previous);
            bool mismatch=previous?.CurrentSourceHashMismatch ?? false,recovered=false;
            string? actualSha=previous?.CurrentActualSha256;
            string working=Path.Combine(state,"item-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(working);
            try
            {
                // A checkpoint alone never skips verification: destination and every metadata version are re-read.
                if(previous?.Status=="Verified" && file.Content is not null && file.Content.ETag.Length>0 && previous.SourceContentETag==file.Content.ETag && NetworkFolderImporter.MetadataFingerprint(previous.Metadata)==NetworkFolderImporter.MetadataFingerprint(file.Current)
                    && await VerifyAsync(target,file,ct))continue;
                if(file.Content is null)throw new MigrationSourceException("SOURCE_CONTENT_MISSING");
                if(previous is not null && file.Current.Version<previous.Metadata.Version)throw new MigrationSourceException("SOURCE_VERSION_REGRESSION");
                if(!await source.MetadataUnchangedAsync(file,ct))throw new MigrationSourceException("SOURCE_CHANGED_DURING_COPY");
                var content=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                string current=Path.Combine(working,"current.bin");
                actualSha=await source.DownloadAsync(file.Content.Id,null,current,Math.Max(file.Content.Size,file.Current.Length),ct);
                content[actualSha]=current;
                bool currentMismatch=!actualSha.Equals(file.Current.Sha256,StringComparison.OrdinalIgnoreCase) || new FileInfo(current).Length!=file.Current.Length;
                mismatch|=currentMismatch;
                if(currentMismatch && !ledger.IntegrityMismatches.Any(x=>x.StorageKey==key && x.ExpectedSha256==file.Current.Sha256 && x.ActualSha256==actualSha))
                {ledger.IntegrityMismatches.Add(new(key,file.Current.Sha256,actualSha,DateTimeOffset.UtcNow));await ledger.SaveAsync(state,ct);}
                var needed=file.Versions.Select(x=>x.Sha256).Append(file.Current.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if(needed.Any(hash=>!content.ContainsKey(hash)))
                {
                    foreach(var version in (await source.NativeVersionsAsync(file.Content.Id,ct)).Skip(1))
                    {
                        if(needed.All(content.ContainsKey))break;
                        string path=Path.Combine(working,Guid.NewGuid().ToString("N")+".bin");
                        try
                        {
                            long maximum=version.Size>=0 ? version.Size : file.Versions.Max(x=>x.Length);
                            string hash=await source.DownloadAsync(file.Content.Id,version.Id,path,maximum,ct);
                            content.TryAdd(hash,path);
                        }
                        catch(MigrationSourceException error) when(error.Code=="GRAPH_HTTP_404") { }
                    }
                }
                foreach(var version in file.Versions)
                {
                    if(!content.TryGetValue(version.Sha256,out var path))throw new MigrationSourceException("SOURCE_VERSION_CONTENT_UNAVAILABLE");
                    await importer.ImportVersionAsync(version,path,ct);
                }
                if(!content.TryGetValue(file.Current.Sha256,out var verified))throw new MigrationSourceException("SOURCE_SHA256_MISMATCH");
                if(!await source.MetadataUnchangedAsync(file,ct))throw new MigrationSourceException("SOURCE_CHANGED_DURING_COPY");
                // Write intent before promotion; a crash can only resume against this exact owned previous record.
                ledger.Files[key]=new(previous?.Metadata ?? file.Current,"Publishing",DateTimeOffset.UtcNow,0,mismatch,actualSha,false,null,previous?.LastPublishedMetadata);
                await ledger.SaveAsync(state,ct);
                await importer.PublishCurrentAsync(file.Current,verified,ct,previous?.LastPublishedMetadata);
                if(!await VerifyAsync(target,file,ct))throw new MigrationSourceException("DESTINATION_VERIFICATION_FAILED");
                recovered=currentMismatch || (previous?.RecoveredFromHistory ?? false);
                ledger.Files[key]=new(file.Current,"Verified",DateTimeOffset.UtcNow,file.Versions.Count,mismatch,actualSha,recovered,null,file.Current,file.Content.ETag);
            }
            catch(MigrationSourceException error)
            {
                ledger.Files[key]=new(file.Current,"Blocked",DateTimeOffset.UtcNow,0,mismatch,actualSha,recovered,error.Code,previous?.LastPublishedMetadata);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) {throw;}
            catch(UnauthorizedAccessException)
            {
                ledger.Files[key]=new(file.Current,"Blocked",DateTimeOffset.UtcNow,0,mismatch,actualSha,recovered,"DESTINATION_PERMISSION_DENIED",previous?.LastPublishedMetadata);
                return ledger;
            }
            catch(StorageException error)
            {
                ledger.Files[key]=new(file.Current,"Blocked",DateTimeOffset.UtcNow,0,mismatch,actualSha,recovered,
                    System.Text.RegularExpressions.Regex.IsMatch(error.Code,"^STORAGE-[A-Z0-9-]{1,90}$")?error.Code:"DESTINATION_STORAGE_FAILED",previous?.LastPublishedMetadata);
            }
            catch(IOException)
            {
                ledger.Files[key]=new(file.Current,"Blocked",DateTimeOffset.UtcNow,0,mismatch,actualSha,recovered,"DESTINATION_IO_FAILED",previous?.LastPublishedMetadata);
                return ledger;
            }
            finally
            {
                // Only this invocation's freshly allocated private download directory is removed.
                Directory.Delete(working,true);await ledger.SaveAsync(state,ct);
            }
        }
        return ledger;
    }
    public static async Task<bool> VerifyAsync(IFileStorage target,SourceFile source,CancellationToken ct)
    {
        var current=await target.GetMetadataAsync(source.Current.StorageKey,ct);
        if(current is null || NetworkFolderImporter.MetadataFingerprint(current)!=NetworkFolderImporter.MetadataFingerprint(source.Current))return false;
        await using(var stream=await target.OpenReadAsync(source.Current.StorageKey,ct))
            if(!Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream,ct)).Equals(source.Current.Sha256,StringComparison.OrdinalIgnoreCase))return false;
        var versions=await target.GetVersionsAsync(source.Current.StorageKey,ct);
        if(target is not IVersionedFileStorage history)return false;
        foreach(var expected in source.Versions)
        {
            var actual=versions.SingleOrDefault(x=>x.Version==expected.Version);
            if(actual is null || NetworkFolderImporter.MetadataFingerprint(actual)!=NetworkFolderImporter.MetadataFingerprint(expected))return false;
            await using var read=await history.OpenVersionAsync(expected.StorageKey,expected.Version,ct);
            if(!Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(read,ct)).Equals(expected.Sha256,StringComparison.OrdinalIgnoreCase))return false;
        }
        return true;
    }
}
