using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RequestPortal.Domain.Requests;

namespace RequestPortal.Infrastructure.Persistence;

internal sealed class QuickIntakeRecordConfiguration : IEntityTypeConfiguration<QuickIntakeRecord>
{
    public void Configure(EntityTypeBuilder<QuickIntakeRecord> builder)
    {
        builder.ToTable("quick_intakes");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.FirstName).HasMaxLength(150).IsRequired();
        builder.Property(item => item.LastName).HasMaxLength(150).IsRequired();
        builder.Property(item => item.MobileNumber).HasMaxLength(40).IsRequired();
        builder.Property(item => item.Parish).HasMaxLength(100).IsRequired();
        builder.Property(item => item.Town).HasMaxLength(150);
        builder.HasIndex(item => item.RequestId).IsUnique();
        builder.HasIndex(item => item.MobileNumber);
        builder.HasIndex(item => item.ConvertedToUserId);
    }
}
