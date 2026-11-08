using chat.Domain.Businesses;
using chat.Domain.Identity;
using chat.Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace chat.Infrastructure.Persistence;

internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> entity)
    {
        entity.HasIndex(row => new { row.ClientUserId, row.BusinessId }).IsUnique();
        entity.HasIndex(row => new { row.ClientUserId, row.UpdatedAt, row.Id });
        entity.HasIndex(row => new { row.BusinessId, row.UpdatedAt, row.Id });
        entity.HasIndex(row => new { row.ClientUserId, row.CreatedAt, row.Id });
        entity.HasIndex(row => new { row.BusinessId, row.CreatedAt, row.Id });
        entity.Property<UserKind>("ClientKind").HasConversion<string>().HasMaxLength(24).HasDefaultValue(UserKind.CLIENT);
        entity.ToTable(table => table.HasCheckConstraint("CK_Conversations_ClientKind", "\"ClientKind\" = 'CLIENT'"));
        entity.HasOne<User>().WithMany().HasForeignKey(nameof(Conversation.ClientUserId), "ClientKind")
            .HasPrincipalKey(nameof(User.Id), nameof(User.Kind)).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Business>().WithMany().HasForeignKey(row => row.BusinessId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> entity)
    {
        entity.HasIndex(row => new { row.ConversationId, row.AuthorUserId, row.ClientMessageId }).IsUnique();
        entity.HasIndex(row => new { row.ConversationId, row.CreatedAt, row.Id });
        entity.HasAlternateKey(row => new { row.ConversationId, row.Id });
        entity.HasOne<Conversation>().WithMany().HasForeignKey(row => row.ConversationId);
        entity.HasOne<User>().WithMany().HasForeignKey(row => row.AuthorUserId).OnDelete(DeleteBehavior.Restrict);
        entity.Property(row => row.AuthorSide).HasConversion<string>().HasMaxLength(8);
        entity.Property(row => row.Type).HasConversion<string>().HasMaxLength(8);
        entity.Property(row => row.Text).HasMaxLength(4000);
    }
}

internal sealed class MessageReadReceiptConfiguration : IEntityTypeConfiguration<MessageReadReceipt>
{
    public void Configure(EntityTypeBuilder<MessageReadReceipt> entity)
    {
        entity.HasKey(row => new { row.ConversationId, row.Side });
        entity.HasOne<Conversation>().WithMany().HasForeignKey(row => row.ConversationId);
        entity.HasOne<Message>().WithMany().HasForeignKey(row => new { row.ConversationId, row.LastMessageId })
            .HasPrincipalKey(row => new { row.ConversationId, row.Id }).OnDelete(DeleteBehavior.Restrict);
        entity.Property(row => row.Side).HasConversion<string>().HasMaxLength(8);
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> entity)
    {
        entity.HasIndex(row => new { row.AvailableAt, row.Id }).HasFilter("\"CompletedAt\" IS NULL");
        entity.Property(row => row.EventName).HasMaxLength(64);
        entity.Property(row => row.Payload).HasColumnType("jsonb");
    }
}
