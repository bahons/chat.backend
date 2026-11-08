using chat.Domain.Identity;
using chat.Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace chat.Infrastructure.Persistence;

internal sealed class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> entity)
    {
        entity.HasOne<Conversation>().WithMany().HasForeignKey(row => row.ConversationId);
        entity.HasOne<User>().WithMany().HasForeignKey(row => row.UploaderUserId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Message>().WithMany().HasForeignKey(row => new { row.ConversationId, row.MessageId })
            .HasPrincipalKey(row => new { row.ConversationId, row.Id }).OnDelete(DeleteBehavior.Restrict);
        entity.Property(row => row.Status).HasConversion<string>().HasMaxLength(16);
        entity.Property(row => row.ContentType).HasMaxLength(32);
        entity.Property(row => row.Checksum).HasMaxLength(64);
        entity.HasIndex(row => new { row.UploadExpiresAt, row.Id }).HasFilter("\"ObjectsDeletedAt\" IS NULL");
    }
}

internal sealed class DeviceRegistrationConfiguration : IEntityTypeConfiguration<DeviceRegistration>
{
    public void Configure(EntityTypeBuilder<DeviceRegistration> entity)
    {
        entity.HasIndex(row => row.PushToken).IsUnique();
        entity.HasIndex(row => new { row.UserId, row.FamilyId });
        entity.HasOne<User>().WithMany().HasForeignKey(row => row.UserId);
        entity.Property(row => row.PushToken).HasMaxLength(128);
    }
}

internal sealed class PushDeliveryConfiguration : IEntityTypeConfiguration<PushDelivery>
{
    public void Configure(EntityTypeBuilder<PushDelivery> entity)
    {
        entity.HasIndex(row => new { row.EventId, row.DeviceId }).IsUnique();
        entity.HasIndex(row => new { row.AvailableAt, row.Id }).HasFilter("\"CompletedAt\" IS NULL");
        entity.HasOne<DeviceRegistration>().WithMany().HasForeignKey(row => row.DeviceId);
        entity.HasOne<Conversation>().WithMany().HasForeignKey(row => row.ConversationId);
        entity.Property(row => row.TicketId).HasMaxLength(100);
    }
}
