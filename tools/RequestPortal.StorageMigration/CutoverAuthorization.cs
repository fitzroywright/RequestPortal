using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RequestPortal.StorageMigration;

public sealed record CutoverPlan(
    int PlanVersion,string Scope,string CandidateCommit,string DatabaseHost,string DatabaseName,
    string SharePointDriveId,string SharePointRoot,string DestinationAlias,string ExpectedMountRoot,
    string StateDirectory,string SourceFingerprint,string ReferenceFingerprint,
    bool PermitVerifiedFinalDelta,bool KeepSharePointSources,bool DeleteSourceDocuments,bool ModifyFfpjm,bool DeployBridge,
    DateTimeOffset ExpiresUtc,IReadOnlyList<string> ApprovedRecoveredIntegrityIssues);

public static class CutoverAuthorization
{
    public static string IntegrityFingerprint(MigrationLedger.IntegrityObservation issue)=>Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(issue.StorageKey+"\0"+issue.ExpectedSha256+"\0"+issue.ActualSha256)));
    public static async Task<CutoverPlan> ReadAsync(string path,string suppliedSha,CancellationToken ct)
    {
        byte[] bytes=await File.ReadAllBytesAsync(path,ct);
        if(bytes.Length>1024*1024 || !Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(suppliedSha,StringComparison.Ordinal))
            throw new MigrationSourceException("EXPLICIT_ADMIN_CUTOVER_AUTHORIZATION_REQUIRED");
        return JsonSerializer.Deserialize<CutoverPlan>(bytes,new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow})
            ?? throw new MigrationSourceException("CUTOVER_PLAN_INVALID");
    }
    public static void Verify(CutoverPlan plan,string executingCommit,string state,string destination,string expectedMountRoot,
        SourceInventory inventory,ReferenceEvidence references,MigrationLedger ledger)
    {
        if(plan.PlanVersion!=1 || plan.Scope!="RequestPortalOnly" || plan.CandidateCommit!=executingCommit ||
           plan.DatabaseHost!="axios.fftpj.local" || plan.DatabaseName!="requestportal" ||
           plan.SharePointDriveId!=inventory.DriveId || plan.SharePointRoot!=inventory.RootFolder ||
           Path.GetFullPath(plan.DestinationAlias)!=Path.GetFullPath(destination) || Path.GetFullPath(plan.StateDirectory)!=Path.GetFullPath(state) ||
           plan.ExpectedMountRoot!=expectedMountRoot || plan.ExpiresUtc<=DateTimeOffset.UtcNow ||
           !plan.KeepSharePointSources || plan.DeleteSourceDocuments || plan.ModifyFfpjm || plan.DeployBridge ||
           plan.SourceFingerprint.Length!=64 || plan.ReferenceFingerprint.Length!=64)
            throw new MigrationSourceException("CUTOVER_PLAN_SCOPE_OR_IDENTITY_MISMATCH");
        if(!plan.PermitVerifiedFinalDelta && (plan.SourceFingerprint!=inventory.SourceFingerprint || plan.ReferenceFingerprint!=references.Fingerprint))
            throw new MigrationSourceException("CUTOVER_PLAN_SOURCE_CHANGED");
        if(inventory.Problems.Count!=0 || references.MissingSourceReferences!=0 || references.AssociationMismatches!=0 ||
           !ledger.FinalSourceFenceVerified || ledger.SourceFingerprint!=inventory.SourceFingerprint ||
           inventory.Files.Any(x=>!ledger.Files.TryGetValue(x.Current.StorageKey,out var entry) || entry.Status!="Verified"))
            throw new MigrationSourceException("CUTOVER_RECONCILIATION_NOT_COMPLETE");
        var approved=plan.ApprovedRecoveredIntegrityIssues?.ToHashSet(StringComparer.Ordinal) ?? [];
        foreach(var issue in ledger.IntegrityMismatches)
            if(!approved.Contains(IntegrityFingerprint(issue)))throw new MigrationSourceException("NEW_SOURCE_INTEGRITY_ISSUE_REQUIRES_REVIEW");
    }
}
