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
        builder.Property(organization => organization.TaxRegistrationNumber).HasMaxLength(100);
        builder.Property(organization => organization.OrganizationType).HasMaxLength(100);
        builder.Property(organization => organization.TradingName).HasMaxLength(250);
        builder.Property(organization => organization.CountryOfRegistration).HasMaxLength(100);
        builder.Property(organization => organization.Address).HasMaxLength(1000);
        builder.Property(organization => organization.Parish).HasMaxLength(100);
        builder.Property(organization => organization.DistrictArea).HasMaxLength(200);
        builder.Property(organization => organization.MailingAddress).HasMaxLength(1000);
        builder.Property(organization => organization.Phone).HasMaxLength(100);
        builder.Property(organization => organization.AlternativePhone).HasMaxLength(100);
        builder.Property(organization => organization.Email).HasMaxLength(320);
        builder.Property(organization => organization.Website).HasMaxLength(500);
        builder.Property(organization => organization.PrimaryContactName).HasMaxLength(250);
        builder.Property(organization => organization.PrimaryContactTitle).HasMaxLength(150);
        builder.Property(organization => organization.AuthorizedSignatoryName).HasMaxLength(250);
        builder.Property(organization => organization.AuthorizedSignatoryTitle).HasMaxLength(150);
        builder.Property(organization => organization.SecondaryContactName).HasMaxLength(250);
        builder.Property(organization => organization.SecondaryContactEmail).HasMaxLength(320);
        builder.Property(organization => organization.SecondaryContactPhone).HasMaxLength(100);
        builder.Property(organization => organization.GeographicServiceArea).HasMaxLength(1000);
        builder.Property(organization => organization.MainActivities).HasMaxLength(4000);
        builder.Property(organization => organization.PopulationGroupsServed).HasMaxLength(2000);
        builder.Property(organization => organization.PrimaryFundingSources).HasMaxLength(2000);
        builder.Property(organization => organization.PremisesSituation).HasMaxLength(500);
        builder.Property(organization => organization.AnnualOperatingBudget).HasPrecision(18, 2);
        builder.Property(organization => organization.ApprovalNotes).HasMaxLength(4000);
        builder.Property(organization => organization.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.HasIndex(organization => new { organization.CountryCode, organization.Name });
        builder.HasIndex(organization => organization.RegistrationNumber);
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
        builder.Property(membership => membership.ReceiveOrganizationNotifications).HasDefaultValue(false).IsRequired();
        builder.Property(membership => membership.ReceiveRequestNotifications).HasDefaultValue(false).IsRequired();
        builder.Property(membership => membership.ReceiveMembershipNotifications).HasDefaultValue(false).IsRequired();
        builder.HasIndex(membership => new { membership.OrganizationId, membership.UserId }).IsUnique();
        builder.HasIndex(membership => membership.UserId);
    }
}
