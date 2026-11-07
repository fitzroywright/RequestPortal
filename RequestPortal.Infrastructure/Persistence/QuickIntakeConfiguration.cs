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
        builder.Property(item => item.DateOfBirth).IsRequired();
        builder.Property(item => item.Gender).HasMaxLength(32).IsRequired();
        builder.Property(item => item.MaritalStatus).HasMaxLength(32).IsRequired();
        builder.Property(item => item.OriginalTelephone).HasMaxLength(40);
        builder.Property(item => item.OriginalSecondaryTelephone).HasMaxLength(40);
        builder.Property(item => item.MobileNumber).HasMaxLength(40).IsRequired();
        builder.Property(item => item.SecondaryTelephone).HasMaxLength(40);
        builder.Property(item => item.Email).HasMaxLength(320);
        builder.Property(item => item.StreetAddress).HasMaxLength(250).IsRequired();
        builder.Property(item => item.CommunityDistrict).HasMaxLength(150).IsRequired();
        builder.Property(item => item.Parish).HasMaxLength(100).IsRequired();
        builder.Property(item => item.EmploymentStatus).HasMaxLength(50).IsRequired();
        builder.Property(item => item.MonthlyIncome).HasPrecision(14, 2).IsRequired();
        builder.Property(item => item.PayFrequency);
        builder.Property(item => item.PhoneLineType);
        builder.Property(item => item.AssistancePrograms).HasMaxLength(500).IsRequired();
        builder.Property(item => item.HelpCategory).HasMaxLength(100).IsRequired();
        builder.HasIndex(item => item.RequestId).IsUnique();
        builder.HasIndex(item => item.MobileNumber);
        builder.HasIndex(item => item.ConvertedToUserId);
    }
}
