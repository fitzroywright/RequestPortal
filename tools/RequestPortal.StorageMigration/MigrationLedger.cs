using System.Text.Json;
using Common.Storage;

namespace RequestPortal.StorageMigration;

public sealed record TransferRecord(StoredFile Metadata,string Status,DateTimeOffset UpdatedUtc,
    int VerifiedVersions,bool CurrentSourceHashMismatch,string? CurrentActualSha256,bool RecoveredFromHistory,string? ErrorCode,StoredFile? LastPublishedMetadata=null,string? SourceContentETag=null);
public sealed class MigrationLedger
{
    public string SourceFingerprint {get;set;}="";
    public Dictionary<string,TransferRecord> Files {get;set;}=new(StringComparer.Ordinal);
    public List<string> Problems {get;set;}=[];
    public List<IntegrityObservation> IntegrityMismatches {get;set;}=[];
    public sealed record IntegrityObservation(string StorageKey,string ExpectedSha256,string ActualSha256,DateTimeOffset ObservedUtc);
    public DateTimeOffset UpdatedUtc {get;set;}
    public string? ReferenceFingerprint {get;set;}
    public bool FinalSourceFenceVerified {get;set;}
    public static async Task<MigrationLedger> LoadAsync(string directory,CancellationToken ct)
    {
        string path=Path.Combine(directory,"ledger.json");
        return File.Exists(path)?JsonSerializer.Deserialize<MigrationLedger>(await File.ReadAllTextAsync(path,ct))
            ?? throw new MigrationSourceException("LEDGER_INVALID"):new();
    }
    public async Task SaveAsync(string directory,CancellationToken ct)
    {
        UpdatedUtc=DateTimeOffset.UtcNow;
        string target=Path.Combine(directory,"ledger.json"),candidate=target+".writing";
        await File.WriteAllTextAsync(candidate,JsonSerializer.Serialize(this,new JsonSerializerOptions {WriteIndented=true}),ct);
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(candidate,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        File.Move(candidate,target,true);
    }
    public object Summary()=>new {
        trackedFiles=Files.Count,verifiedFiles=Files.Values.Count(x=>x.Status=="Verified"),
        verifiedVersions=Files.Values.Where(x=>x.Status=="Verified").Sum(x=>x.VerifiedVersions),
        outstandingTransfers=Files.Values.Count(x=>x.Status!="Verified"),
        observedSourceHashMismatches=IntegrityMismatches.Select(x=>x.StorageKey).Distinct(StringComparer.Ordinal).Count(),
        recoveredFromVerifiedHistory=Files.Values.Count(x=>x.RecoveredFromHistory),
        missingFiles=Files.Values.Count(x=>x.ErrorCode=="SOURCE_CONTENT_MISSING"),
        problems=Problems.Count,FinalSourceFenceVerified,UpdatedUtc};
}
