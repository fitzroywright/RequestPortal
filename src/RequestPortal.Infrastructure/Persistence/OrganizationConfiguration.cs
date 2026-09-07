using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RequestPortal.Domain.Organizations;

namespace RequestPortal.Infrastructure.Persistence;

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("organizations");
        builder.HasKey(organization => organization.Id);
        builder.Property(organization => organization.Name).HasMaxLength(250).IsRequired();
        builder.Property(organization => organization.CountryCode).HasMaxLength(2).IsRequired();
        builder.Property(organization => organization.RegistrationNumber).HasMaxLength(100);
        builder.Property(organization => organization.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.HasIndex(organization => new { organization.CountryCode, organization.Name });
        builder.HasMany(organization => organization.Memberships)
            .WithOne()
            .HasForeignKey(membership => membership.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OrganizationMembershipConfiguration : IEntityTypeConfiguration<OrganizationMembership>
{
    public void Configure(EntityTypeBuilder<OrganizationMembership> builder)
    {
        builder.ToTable("organization_memberships");
        builder.HasKey(membership => membership.Id);
        builder.Property(membership => membership.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(membership => membership.Role).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.HasIndex(membership => new { membership.OrganizationId, membership.UserId }).IsUnique();
        builder.HasIndex(membership => membership.UserId);
    }
}
