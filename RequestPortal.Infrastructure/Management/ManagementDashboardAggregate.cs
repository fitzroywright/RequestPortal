using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace RequestPortal.Infrastructure.Management;

public sealed class ManagementDashboardAggregate
{
    public int Id { get; set; } = 1;
    public long SnapshotVersion { get; set; }
    public string? AggregateData { get; set; }
    public DateTimeOffset? LastUpdatedUtc { get; set; }
    public DateTimeOffset? LastReconciledUtc { get; set; }
    public string RefreshStatus { get; set; } = "Pending";
    public string? LastError { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset NextRefreshUtc { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

internal sealed class ManagementDashboardAggregateConfiguration : IEntityTypeConfiguration<ManagementDashboardAggregate>
{
    public void Configure(EntityTypeBuilder<ManagementDashboardAggregate> b)
    {
        b.ToTable("management_dashboard_aggregate", t=>t.HasCheckConstraint("CK_management_dashboard_singleton", "\"Id\" = 1"));
        b.HasKey(x=>x.Id);b.Property(x=>x.Id).ValueGeneratedNever();
        b.Property(x=>x.AggregateData).HasColumnType("jsonb");
        b.Property(x=>x.RefreshStatus).HasMaxLength(32);b.Property(x=>x.LastError).HasMaxLength(500);
    }
}
