using chat.Domain.Businesses;
using chat.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace chat.Infrastructure.Persistence;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> entity)
    {
        entity.HasIndex(user => user.NormalizedEmail).IsUnique();
        entity.HasIndex(user => user.VerificationHash).IsUnique();
        entity.HasIndex(user => user.ResetHash).IsUnique();
        entity.Property(user => user.Email).HasMaxLength(254);
        entity.Property(user => user.NormalizedEmail).HasMaxLength(254);
        entity.Property(user => user.DisplayName).HasMaxLength(100);
        entity.Property(user => user.Kind).HasConversion<string>().HasMaxLength(24);
    }
}

internal sealed class RefreshSessionConfiguration : IEntityTypeConfiguration<RefreshSession>
{
    public void Configure(EntityTypeBuilder<RefreshSession> entity)
    {
        entity.HasIndex(session => session.TokenHash).IsUnique();
        entity.HasIndex(session => new { session.UserId, session.FamilyId });
        entity.HasIndex(session => new { session.ExpiresAt, session.Id });
        entity.Property(session => session.TokenHash).HasMaxLength(64);
        entity.HasOne<User>().WithMany().HasForeignKey(session => session.UserId);
    }
}

internal sealed class BusinessMembershipConfiguration : IEntityTypeConfiguration<BusinessMembership>
{
    public void Configure(EntityTypeBuilder<BusinessMembership> entity)
    {
        entity.HasKey(membership => new { membership.UserId, membership.BusinessId });
        entity.HasOne<Business>().WithMany().HasForeignKey(membership => membership.BusinessId);
        entity.Property(membership => membership.Role).HasConversion<string>().HasMaxLength(12);
        entity.HasOne<User>().WithMany().HasForeignKey(membership => membership.UserId);
    }
}
