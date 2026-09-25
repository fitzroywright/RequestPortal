using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RequestPortal.Domain.Reviewers;

namespace RequestPortal.Infrastructure.Persistence;

public sealed class ExternalReviewerConfiguration : IEntityTypeConfiguration<ExternalReviewerInvitation>
{
    public void Configure(EntityTypeBuilder<ExternalReviewerInvitation> builder)
    {
        builder.ToTable("external_reviewer_invitations");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Email).HasMaxLength(320).IsRequired();
        builder.Property(item => item.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(item => item.Scope).HasMaxLength(100).IsRequired();
        builder.Property(item => item.TokenHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(item => item.Email);
        builder.HasIndex(item => item.ExpiresUtc);
        builder.HasIndex(item => item.RevokedUtc);
        builder.HasIndex(item => item.OrganizationId);
        builder.HasIndex(item => item.RequestId);
    }
}
