using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RequestPortal.Domain.ReferenceData;

namespace RequestPortal.Infrastructure.Persistence;

internal sealed class RequestCategoryConfiguration : IEntityTypeConfiguration<RequestCategory>
{
    public void Configure(EntityTypeBuilder<RequestCategory> builder)
    {
        builder.ToTable("request_categories");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Name).HasMaxLength(150).IsRequired();
        builder.Property(item => item.ParentName).HasMaxLength(150);
        builder.HasIndex(item => new { item.Name, item.ParentName }).IsUnique();
    }
}

internal sealed class ProgramClassificationConfiguration : IEntityTypeConfiguration<ProgramClassification>
{
    public void Configure(EntityTypeBuilder<ProgramClassification> builder)
    {
        builder.ToTable("program_classifications");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Name).HasMaxLength(150).IsRequired();
        builder.HasIndex(item => item.Name).IsUnique();
    }
}

internal sealed class UnitOfMeasureConfiguration : IEntityTypeConfiguration<UnitOfMeasure>
{
    public void Configure(EntityTypeBuilder<UnitOfMeasure> builder)
    {
        builder.ToTable("units_of_measure");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Code).HasMaxLength(20).IsRequired();
        builder.Property(item => item.Name).HasMaxLength(100).IsRequired();
        builder.HasIndex(item => item.Code).IsUnique();
    }
}
