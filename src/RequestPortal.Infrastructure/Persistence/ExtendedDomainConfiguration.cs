using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RequestPortal.Domain.Auditing;
using RequestPortal.Domain.Beneficiaries;
using RequestPortal.Domain.Documents;
using RequestPortal.Domain.Fulfilment;
using RequestPortal.Domain.Integration;
using RequestPortal.Domain.Notifications;

namespace RequestPortal.Infrastructure.Persistence;

internal sealed class BeneficiaryConfiguration : IEntityTypeConfiguration<Beneficiary>
{
    public void Configure(EntityTypeBuilder<Beneficiary> builder)
    {
        builder.ToTable("beneficiaries");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.FirstName).HasMaxLength(150).IsRequired();
        builder.Property(item => item.LastName).HasMaxLength(150).IsRequired();
        builder.Property(item => item.ParentOrGuardian).HasMaxLength(250);
        builder.Property(item => item.Location).HasMaxLength(500);
        builder.HasIndex(item => item.OrganizationId);
        builder.HasIndex(item => new { item.OrganizationId, item.LastName, item.FirstName });
    }
}

internal sealed class StoredDocumentConfiguration : IEntityTypeConfiguration<StoredDocument>
{
    public void Configure(EntityTypeBuilder<StoredDocument> builder)
    {
        builder.ToTable("documents");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.OwnerType).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(item => item.DocumentType).HasConversion<string>().HasMaxLength(64).IsRequired();
        builder.Property(item => item.OriginalFileName).HasMaxLength(512).IsRequired();
        builder.Property(item => item.ContentType).HasMaxLength(200).IsRequired();
        builder.Property(item => item.StorageKey).HasMaxLength(1000).IsRequired();
        builder.HasIndex(item => new { item.OrganizationId, item.OwnerType, item.OwnerId });
    }
}

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("audit_entries");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Action).HasMaxLength(100).IsRequired();
        builder.Property(item => item.EntityType).HasMaxLength(100).IsRequired();
        builder.Property(item => item.Detail).HasMaxLength(4000);
        builder.HasIndex(item => new { item.OrganizationId, item.OccurredUtc });
        builder.HasIndex(item => new { item.EntityType, item.EntityId, item.OccurredUtc });
    }
}

internal sealed class FulfilmentConfiguration : IEntityTypeConfiguration<Fulfilment>
{
    public void Configure(EntityTypeBuilder<Fulfilment> builder)
    {
        builder.ToTable("fulfilments");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Source).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(item => item.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(item => item.ExternalReference).HasMaxLength(200);
        builder.Property(item => item.Notes).HasMaxLength(4000);
        builder.HasIndex(item => item.RequestId).IsUnique();
        builder.HasIndex(item => item.OrganizationId);
    }
}

internal sealed class IntegrationLinkConfiguration : IEntityTypeConfiguration<IntegrationLink>
{
    public void Configure(EntityTypeBuilder<IntegrationLink> builder)
    {
        builder.ToTable("integration_links");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.SystemName).HasMaxLength(100).IsRequired();
        builder.Property(item => item.ExternalReference).HasMaxLength(200);
        builder.Property(item => item.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(item => item.LastError).HasMaxLength(4000);
        builder.HasIndex(item => new { item.RequestId, item.SystemName }).IsUnique();
    }
}

internal sealed class NotificationPreferenceConfiguration : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> builder)
    {
        builder.ToTable("notification_preferences");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Channel).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(item => item.Destination).HasMaxLength(500).IsRequired();
        builder.HasIndex(item => new { item.UserId, item.Channel }).IsUnique();
    }
}
