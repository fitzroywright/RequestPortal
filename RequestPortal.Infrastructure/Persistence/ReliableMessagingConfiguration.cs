using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace RequestPortal.Infrastructure.Persistence;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(message => message.Id);
        builder.Property(message => message.MessageType).HasMaxLength(500).IsRequired();
        builder.Property(message => message.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(message => message.LastError).HasMaxLength(4000);
        builder.HasIndex(message => new { message.ProcessedUtc, message.OccurredUtc });
        builder.HasIndex(message => new { message.ProcessedUtc, message.NextAttemptUtc, message.OccurredUtc });
        builder.HasIndex(message => message.RequestId);
    }
}

internal sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("inbox_messages");

        builder.HasKey(message => new
        {
            message.MessageId,
            message.Consumer
        });

        builder.Property(message => message.Consumer).HasMaxLength(250).IsRequired();
        builder.HasIndex(message => message.ProcessedUtc);
    }
}
