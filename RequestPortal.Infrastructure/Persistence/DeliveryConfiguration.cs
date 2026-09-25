using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RequestPortal.Domain.Delivery;

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
        builder.Property(item => item.SnapshotJson).HasColumnType("jsonb").IsRequired();
        builder.Property(item => item.AcknowledgedBy).HasMaxLength(240);
        builder.Property(item => item.SignatureData).HasColumnType("text");
        builder.Property(item => item.Notes).HasMaxLength(2000);
        builder.HasIndex(item => item.RequestId);
        builder.HasIndex(item => item.DocumentReference);
        builder.HasIndex(item => new { item.Status, item.SyncedToFfpManagerUtc });
    }
}
