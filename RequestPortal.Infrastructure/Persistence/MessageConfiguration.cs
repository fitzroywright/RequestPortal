using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RequestPortal.Domain.Messages;

namespace RequestPortal.Infrastructure.Persistence;

internal sealed class MessageThreadConfiguration : IEntityTypeConfiguration<MessageThread>
{
    public void Configure(EntityTypeBuilder<MessageThread> builder)
    {
        builder.ToTable("message_threads");
        builder.HasKey(thread => thread.Id);
        builder.Property(thread => thread.Subject).HasMaxLength(300).IsRequired();
        builder.HasIndex(thread => thread.OrganizationId);
        builder.HasIndex(thread => thread.RequestId);
        builder.HasIndex(thread => new { thread.StaffVisible, thread.CreatedUtc });
    }
}

internal sealed class PortalMessageConfiguration : IEntityTypeConfiguration<PortalMessage>
{
    public void Configure(EntityTypeBuilder<PortalMessage> builder)
    {
        builder.ToTable("portal_messages");
        builder.HasKey(message => message.Id);
        builder.Property(message => message.SenderKind).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(message => message.SenderDisplayName).HasMaxLength(250).IsRequired();
        builder.Property(message => message.Body).HasColumnType("text").IsRequired();
        builder.HasIndex(message => new { message.ThreadId, message.SentUtc });
        builder.HasOne<MessageThread>()
            .WithMany()
            .HasForeignKey(message => message.ThreadId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class MessageRecipientConfiguration : IEntityTypeConfiguration<MessageRecipient>
{
    public void Configure(EntityTypeBuilder<MessageRecipient> builder)
    {
        builder.ToTable("message_recipients");
        builder.HasKey(recipient => new { recipient.MessageId, recipient.UserId });
        builder.HasIndex(recipient => new { recipient.UserId, recipient.ReadUtc });
        builder.HasOne<PortalMessage>()
            .WithMany()
            .HasForeignKey(recipient => recipient.MessageId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}


internal sealed class MessageAttachmentConfiguration : IEntityTypeConfiguration<MessageAttachment>
{
    public void Configure(EntityTypeBuilder<MessageAttachment> builder)
    {
        builder.ToTable("message_attachments");
        builder.HasKey(attachment => new { attachment.MessageId, attachment.DocumentId });
        builder.HasIndex(attachment => attachment.DocumentId);
        builder.HasOne<PortalMessage>()
            .WithMany()
            .HasForeignKey(attachment => attachment.MessageId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
