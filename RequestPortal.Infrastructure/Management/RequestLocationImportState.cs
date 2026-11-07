using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace RequestPortal.Infrastructure.Management;

/// <summary>Proof that the one-time geography recovery finished before PostgreSQL becomes authoritative.</summary>
public sealed class RequestLocationImportState
{
    public int Id { get; set; } = 1;
    public string SourceFingerprint { get; set; } = "";
    public DateTimeOffset VerifiedUtc { get; set; }
    public int ImportedLocations { get; set; }
}

internal sealed class RequestLocationImportStateConfiguration : IEntityTypeConfiguration<RequestLocationImportState>
{
    public void Configure(EntityTypeBuilder<RequestLocationImportState> builder)
    {
        builder.ToTable("request_location_import_state", table => table.HasCheckConstraint("CK_request_location_import_singleton", "\"Id\" = 1"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.SourceFingerprint).HasMaxLength(64).IsRequired();
    }
}
