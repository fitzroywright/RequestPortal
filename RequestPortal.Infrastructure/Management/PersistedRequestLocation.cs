using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RequestPortal.Application.Management;
using RequestPortal.Domain.Requests;

namespace RequestPortal.Infrastructure.Management;

/// <summary>Authoritative request geography; file providers are never consulted by dashboards.</summary>
public sealed class PersistedRequestLocation
{
    public Guid RequestId { get; set; }
    public string? SourceText { get; set; }
    public string Parish { get; set; } = "";
    public string? Community { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public LocationPrecision Precision { get; set; }
    public DateTimeOffset ResolvedUtc { get; set; }
    public string? ImportedStorageKey { get; set; }
    public string? ImportedSha256 { get; set; }
    public DateTimeOffset? ImportedUtc { get; set; }

    public RequestLocationSnapshot Snapshot() => new(RequestId, SourceText, Parish, Community, Latitude, Longitude, Precision, ResolvedUtc);
    public void Apply(RequestLocationSnapshot snapshot)
    {
        if (snapshot.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(snapshot.Parish) ||
            !Enum.IsDefined(snapshot.Precision) || snapshot.ResolvedUtc == default ||
            snapshot.Latitude is < -90 or > 90 || snapshot.Longitude is < -180 or > 180 ||
            snapshot.Latitude.HasValue != snapshot.Longitude.HasValue)
            throw new InvalidOperationException("Request location validation failed.");
        RequestId = snapshot.RequestId; SourceText = snapshot.SourceText; Parish = snapshot.Parish;
        Community = snapshot.Community; Latitude = snapshot.Latitude; Longitude = snapshot.Longitude;
        Precision = snapshot.Precision; ResolvedUtc = snapshot.ResolvedUtc;
    }
}

internal sealed class PersistedRequestLocationConfiguration : IEntityTypeConfiguration<PersistedRequestLocation>
{
    public void Configure(EntityTypeBuilder<PersistedRequestLocation> b)
    {
        b.ToTable("request_locations", table =>
        {
            table.HasCheckConstraint("CK_request_location_coordinates", "(\"Latitude\" IS NULL) = (\"Longitude\" IS NULL) AND (\"Latitude\" IS NULL OR \"Latitude\" BETWEEN -90 AND 90) AND (\"Longitude\" IS NULL OR \"Longitude\" BETWEEN -180 AND 180)");
            table.HasCheckConstraint("CK_request_location_precision", "\"Precision\" BETWEEN 0 AND 3");
            table.HasCheckConstraint("CK_request_location_parish", "length(btrim(\"Parish\")) > 0");
        });
        b.HasKey(x => x.RequestId);
        b.HasOne<AssistanceRequest>().WithOne().HasForeignKey<PersistedRequestLocation>(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.Latitude).HasColumnType("numeric"); b.Property(x => x.Longitude).HasColumnType("numeric");
        b.Property(x => x.ImportedStorageKey).HasMaxLength(1000); b.Property(x => x.ImportedSha256).HasMaxLength(64);
    }
}
