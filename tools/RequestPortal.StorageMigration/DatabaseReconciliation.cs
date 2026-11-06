using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Common.Storage;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Management;
using RequestPortal.Infrastructure.Management;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.StorageMigration;

public sealed record StorageReference(string Kind,Guid Id,string Key,long? Length,Guid? OrganizationId,Guid? OwnerId,string? OwnerType);
public sealed record ReferenceEvidence(string Fingerprint,int Documents,int ProfilePhotos,int MissingSourceReferences,
    int AssociationMismatches,IReadOnlyList<string> Problems);

public static class DatabaseReconciliation
{
    public static async Task<ReferenceEvidence> ReadAsync(RequestPortalDbContext db,SourceInventory inventory,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY; SET LOCAL statement_timeout='30s'",ct);
        var documents=await db.Documents.AsNoTracking().OrderBy(x=>x.Id)
            .Select(x=>new StorageReference("Document",x.Id,x.StorageKey,x.SizeBytes,x.OrganizationId,x.OwnerId,x.OwnerType.ToString())).ToListAsync(ct);
        var profiles=await db.Users.AsNoTracking().Where(x=>x.ProfilePhotoStorageKey!=null).OrderBy(x=>x.Id)
            .Select(x=>new StorageReference("ProfilePhoto",x.Id,x.ProfilePhotoStorageKey!,null,null,x.Id,"ApplicantProfile")).ToListAsync(ct);
        await tx.RollbackAsync(ct);
        var files=inventory.Files.ToDictionary(x=>x.Current.StorageKey,StringComparer.Ordinal);
        var problems=new List<string>();int missing=0,mismatched=0;
        foreach(var reference in documents.Concat(profiles))
        {
            if(!files.TryGetValue(reference.Key,out var file) || file.Content is null) {missing++;problems.Add("MISSING_REFERENCE:"+reference.Id);continue;}
            var metadata=file.Current;
            bool bad=reference.Length is { } length && metadata.Length!=length;
            foreach(var pair in new[] {("OrganizationId",reference.OrganizationId),("OwnerId",reference.OwnerId)})
                if(pair.Item2 is { } expected && metadata.Metadata.TryGetValue(pair.Item1,out var value) && (!Guid.TryParse(value,out var actual)||actual!=expected))bad=true;
            if(metadata.Metadata.TryGetValue("OwnerType",out var ownerType) && reference.OwnerType is not null && !ownerType.Equals(reference.OwnerType,StringComparison.OrdinalIgnoreCase))bad=true;
            if(reference.Kind=="ProfilePhoto" && metadata.Metadata.TryGetValue("UserId",out var userId) && (!Guid.TryParse(userId,out var actualUser) || actualUser!=reference.OwnerId))bad=true;
            if(metadata.Metadata.TryGetValue("Application",out var application) && !application.Equals("RequestPortal",StringComparison.OrdinalIgnoreCase))bad=true;
            if(bad) {mismatched++;problems.Add("ASSOCIATION_MISMATCH:"+reference.Id);}
        }
        string fingerprint=Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(documents.Concat(profiles).ToArray())));
        return new(fingerprint,documents.Count,profiles.Count,missing,mismatched,problems);
    }
    public static async Task<object> ImportLocationsAsync(RequestPortalDbContext db,SourceInventory inventory,string destination,string authorizedPlanSha256,string referenceFingerprint,CancellationToken ct)
    {
        var target=new NetworkFolderStorage(destination);
        int imported=0,unchanged=0,orphans=0,derived=0,invalid=0;
        var requestIds=await db.AssistanceRequests.Select(x=>x.Id).ToHashSetAsync(ct);
        // All source payloads were imported and SHA-256 verified before this additive DB import.
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,ct);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout='5s'; SET LOCAL statement_timeout='60s'",ct);
        bool locked=await db.Database.SqlQueryRaw<bool>("SELECT pg_try_advisory_xact_lock(7294321652::bigint) AS \"Value\"").SingleAsync(ct);
        if(!locked)throw new MigrationSourceException("LOCATION_IMPORT_BUSY");
        foreach(var file in inventory.Files.Where(x=>x.Current.StorageKey.StartsWith("requestportal/locations/",StringComparison.Ordinal) && x.Current.StorageKey.EndsWith("/resolved-location.json",StringComparison.Ordinal)))
        {
            await using var stream=await target.OpenReadAsync(file.Current.StorageKey,ct);
            RequestLocationSnapshot? snapshot;
            try {snapshot=await JsonSerializer.DeserializeAsync<RequestLocationSnapshot>(stream,new JsonSerializerOptions(JsonSerializerDefaults.Web),ct);}
            catch(JsonException) {invalid++;continue;}
            string[] parts=file.Current.StorageKey.Split('/');
            if(snapshot is null || parts.Length!=4 || !Guid.TryParseExact(parts[2],"N",out var keyId) || snapshot.RequestId!=keyId)
                throw new MigrationSourceException("LOCATION_SOURCE_IDENTITY_MISMATCH");
            if(file.Current.Metadata.TryGetValue("RequestId",out var declaredRequest) && (!Guid.TryParse(declaredRequest,out var declaredId) || declaredId!=snapshot.RequestId))
                throw new MigrationSourceException("LOCATION_METADATA_IDENTITY_MISMATCH");
            if(!requestIds.Contains(snapshot.RequestId)) {orphans++;continue;}
            var row=await db.RequestLocations.SingleOrDefaultAsync(x=>x.RequestId==snapshot.RequestId,ct);
            if(row is not null && row.ResolvedUtc>=snapshot.ResolvedUtc)
            {
                if(row.ImportedStorageKey is not null && row.ImportedSha256!=file.Current.Sha256)throw new MigrationSourceException("LOCATION_IMPORT_CONFLICT");
                unchanged++;continue;
            }
            if(row is null) {row=new();db.RequestLocations.Add(row);}
            row.Apply(snapshot);row.ImportedStorageKey=file.Current.StorageKey;row.ImportedSha256=file.Current.Sha256;row.ImportedUtc=DateTimeOffset.UtcNow;
            imported++;
        }
        // The existing dashboard already derives parish/community from PostgreSQL request text when no legacy file exists.
        // Persist that same result once; never invent exact coordinates or rewrite the request's source text.
        var requests=await db.AssistanceRequests.AsNoTracking().Select(x=>new {x.Id,x.Location}).ToListAsync(ct);
        var known=await db.RequestLocations.Select(x=>x.RequestId).ToHashSetAsync(ct);
        known.UnionWith(db.ChangeTracker.Entries<PersistedRequestLocation>().Select(x=>x.Entity.RequestId));
        foreach(var request in requests.Where(x=>!known.Contains(x.Id)))
        {
            var legacy=ResolveText(request.Id,request.Location);
            if(legacy is null)continue;
            var row=new PersistedRequestLocation();row.Apply(legacy);db.RequestLocations.Add(row);derived++;
        }
        if(invalid>0)throw new MigrationSourceException("LEGACY_LOCATION_JSON_INVALID");
        var stamp=await db.RequestLocationImportStates.SingleOrDefaultAsync(x=>x.Id==1,ct);
        if(stamp is null) {stamp=new();db.RequestLocationImportStates.Add(stamp);}
        stamp.SourceFingerprint=inventory.SourceFingerprint;stamp.ReferenceFingerprint=referenceFingerprint;
        stamp.AuthorizedPlanSha256=authorizedPlanSha256;stamp.VerifiedUtc=DateTimeOffset.UtcNow;
        stamp.ImportedLocations=imported+unchanged;stamp.DerivedLocations=derived;
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
        return new {status="locations-imported",imported,alreadyCurrent=unchanged,derivedFromExistingPostgresText=derived,
            orphanLegacyLocations=orphans,legacyInvalidJson=invalid,sourceFilesDeleted=0,referenceRowsRewritten=0};
    }
    private static RequestLocationSnapshot? ResolveText(Guid id,string? text)
    {
        // Public, shared legacy-normalization policy; this is not a second geocoder.
        var legacy=RequestLocationService.ResolveExisting(id,text);
        return legacy is null?null:legacy with {ResolvedUtc=DateTimeOffset.UtcNow};
    }
}
