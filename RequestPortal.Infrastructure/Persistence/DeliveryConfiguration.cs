using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RequestPortal.Domain.Delivery;
using RequestPortal.Domain.Requests;

namespace RequestPortal.Infrastructure.Persistence;

public sealed class DeliveryConfiguration : IEntityTypeConfiguration<DeliveryRecord>
{
    public void Configure(EntityTypeBuilder<DeliveryRecord> builder)
    {
        builder.ToTable("deliveries");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.DocumentType).HasMaxLength(64).IsRequired();
        builder.Property(item => item.DocumentReference).HasMaxLength(160).IsRequired();
        builder.Property(item => item.RecipientName).HasMaxLength(240).IsRequired();
        builder.Property(item => item.DeliveryPerson).HasMaxLength(240);
        builder.Property(item => item.FfpDeliveryOrderId).HasMaxLength(160).IsRequired();
        builder.Property(item => item.SnapshotJson).HasColumnType("jsonb").IsRequired();
        builder.Property(item => item.AccessTokenHash).HasMaxLength(64).IsRequired();
        builder.Property(item => item.AcknowledgedBy).HasMaxLength(240);
        builder.Property(item => item.AccessExpiresUtc).IsRequired();
        builder.Property(item => item.SignatureData).HasColumnType("text");
        builder.Property(item => item.Notes).HasMaxLength(2000);
        builder.Property(item => item.RecipientConfirmationJson).HasColumnType("jsonb");
        builder.Property(item => item.RecipientDiscrepancyJson).HasColumnType("jsonb");
        builder.HasOne<AssistanceRequest>()
            .WithMany()
            .HasForeignKey(item => item.RequestId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(item => item.RequestId);
        builder.HasIndex(item => item.DocumentReference);
        builder.HasIndex(item => new { item.RequestId, item.FfpDeliveryOrderId }).IsUnique();
        builder.HasIndex(item => new { item.Status, item.SyncedToFfpManagerUtc });
        builder.HasIndex(item => item.RecipientConfirmationSyncedUtc);
    }
}

public sealed class DeliveryReturnRequestConfiguration : IEntityTypeConfiguration<DeliveryReturnRequest>
{
    public void Configure(EntityTypeBuilder<DeliveryReturnRequest> builder)
    {
        builder.ToTable("delivery_returns");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.FfpDeliveryItemId).HasMaxLength(160);
        builder.Property(item => item.ItemDescription).HasMaxLength(500).IsRequired();
        builder.Property(item => item.Quantity).HasPrecision(18, 3);
        builder.Property(item => item.Reason).HasMaxLength(2000).IsRequired();
        builder.Property(item => item.Status).HasMaxLength(64).IsRequired();
        builder.Property(item => item.FfpReturnId).HasMaxLength(160);
        builder.Property(item => item.StatusDetail).HasMaxLength(2000);
        builder.HasOne<AssistanceRequest>()
            .WithMany()
            .HasForeignKey(item => item.RequestId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<DeliveryRecord>()
            .WithMany()
            .HasForeignKey(item => item.DeliveryId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(item => item.RequestId);
        builder.HasIndex(item => item.DeliveryId);
        builder.HasIndex(item => item.SyncedToFfpManagerUtc);
    }
}
