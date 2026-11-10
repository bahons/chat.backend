using chat.Domain.Identity;
using chat.Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace chat.Infrastructure.Persistence;

internal sealed class WorkConfiguration : IEntityTypeConfiguration<OutboxMessage>,
    IEntityTypeConfiguration<PushDelivery>, IEntityTypeConfiguration<AccountMail>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> entity) => ConfigureLease(entity);

    public void Configure(EntityTypeBuilder<PushDelivery> entity) => ConfigureLease(entity);

    public void Configure(EntityTypeBuilder<AccountMail> entity)
    {
        ConfigureLease(entity);
        entity.HasOne<User>().WithMany().HasForeignKey(row => row.UserId);
        entity.HasIndex(row => new { row.AvailableAt, row.Id }).HasFilter("\"CompletedAt\" IS NULL");
    }

    private static void ConfigureLease<T>(EntityTypeBuilder<T> entity) where T : class, ILeasedWork
    {
        entity.Property(row => row.LeaseId).IsConcurrencyToken();
        entity.Property(row => row.Outcome).HasMaxLength(24);
        entity.HasIndex(row => new { row.CompletedAt, row.Id }).HasFilter("\"CompletedAt\" IS NOT NULL");
    }
}
