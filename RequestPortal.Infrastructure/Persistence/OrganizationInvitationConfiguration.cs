using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RequestPortal.Domain.Organizations;

namespace RequestPortal.Infrastructure.Persistence;

public sealed class OrganizationInvitationConfiguration : IEntityTypeConfiguration<OrganizationInvitation>
{
    public void Configure(EntityTypeBuilder<OrganizationInvitation> builder)
    {
        builder.ToTable("organization_invitations");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Email).HasMaxLength(320).IsRequired();
        builder.Property(item => item.TokenHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(item => item.OrganizationId);
        builder.HasIndex(item => item.Email);
        builder.HasIndex(item => item.ExpiresUtc);
        builder.HasIndex(item => new { item.OrganizationId, item.Email, item.Status });
    }
}
