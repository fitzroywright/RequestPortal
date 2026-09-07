using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RequestPortal.Domain.Requests;

namespace RequestPortal.Infrastructure.Persistence;

internal sealed class AssistanceRequestConfiguration : IEntityTypeConfiguration<AssistanceRequest>
{
    public void Configure(EntityTypeBuilder<AssistanceRequest> builder)
    {
        builder.ToTable("assistance_requests");
        builder.HasKey(request => request.Id);
        builder.Property(request => request.PublicReference).HasMaxLength(40).IsRequired();
        builder.Property(request => request.Summary).HasMaxLength(4000);
        builder.Property(request => request.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.HasIndex(request => request.PublicReference).IsUnique();
        builder.HasIndex(request => new { request.OrganizationId, request.Status });
        builder.HasMany(request => request.Items)
            .WithOne()
            .HasForeignKey(item => item.RequestId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(request => request.StatusHistory)
            .WithOne()
            .HasForeignKey(history => history.RequestId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AssistanceRequestItemConfiguration : IEntityTypeConfiguration<AssistanceRequestItem>
{
    public void Configure(EntityTypeBuilder<AssistanceRequestItem> builder)
    {
        builder.ToTable("assistance_request_items");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Description).HasMaxLength(1000).IsRequired();
        builder.Property(item => item.Quantity).HasPrecision(18, 3);
    }
}

internal sealed class RequestStatusHistoryConfiguration : IEntityTypeConfiguration<RequestStatusHistory>
{
    public void Configure(EntityTypeBuilder<RequestStatusHistory> builder)
    {
        builder.ToTable("request_status_history");
        builder.HasKey(history => history.Id);
        builder.Property(history => history.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(history => history.Reason).HasMaxLength(4000);
        builder.HasIndex(history => new { history.RequestId, history.ChangedUtc });
    }
}
