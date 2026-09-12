using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RequestPortal.Domain.Management;
using RequestPortal.Domain.Requests;

namespace RequestPortal.Infrastructure.Management;

internal sealed class RequestGeoLocationConfiguration : IEntityTypeConfiguration<RequestGeoLocation>
{
    public void Configure(EntityTypeBuilder<RequestGeoLocation> builder)
    {
        builder.ToTable("request_geo_locations");
        builder.HasKey(item => item.RequestId);
        builder.Property(item => item.SourceText).HasMaxLength(500);
        builder.Property(item => item.Parish).HasMaxLength(80).IsRequired();
        builder.Property(item => item.Community).HasMaxLength(150);
        builder.Property(item => item.Latitude).HasPrecision(9, 6);
        builder.Property(item => item.Longitude).HasPrecision(9, 6);
        builder.Property(item => item.Precision).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.HasIndex(item => item.Parish);
        builder.HasIndex(item => new { item.Parish, item.Community });
        builder.HasOne<AssistanceRequest>()
            .WithOne()
            .HasForeignKey<RequestGeoLocation>(item => item.RequestId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ManagementDashboardOptions
{
    public const string SectionName = "ManagementDashboard";
    public int NewRequestHours { get; set; } = 48;
    public int OverdueDays { get; set; } = 14;
    public int StalledDays { get; set; } = 7;
    public int TrendWeeks { get; set; } = 12;
}
