using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace RequestPortal.Infrastructure.Management;

/// <summary>Durable proof that existing geography was reconciled before switching its authoritative store.</summary>
public sealed class RequestLocationImportState
{
    public int Id {get;set;}=1;
    public string SourceFingerprint {get;set;}="";
    public string ReferenceFingerprint {get;set;}="";
    public string AuthorizedPlanSha256 {get;set;}="";
    public DateTimeOffset VerifiedUtc {get;set;}
    public int ImportedLocations {get;set;}
    public int DerivedLocations {get;set;}
}
internal sealed class RequestLocationImportStateConfiguration : IEntityTypeConfiguration<RequestLocationImportState>
{
    public void Configure(EntityTypeBuilder<RequestLocationImportState> b)
    {
        b.ToTable("request_location_import_state",table=>table.HasCheckConstraint("CK_request_location_import_singleton","\"Id\" = 1"));
        b.HasKey(x=>x.Id);b.Property(x=>x.Id).ValueGeneratedNever();
        b.Property(x=>x.SourceFingerprint).HasMaxLength(64).IsRequired();
        b.Property(x=>x.ReferenceFingerprint).HasMaxLength(64).IsRequired();
        b.Property(x=>x.AuthorizedPlanSha256).HasMaxLength(64).IsRequired();
    }
}
