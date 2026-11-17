using chat.Domain.Businesses;
using chat.Domain.Identity;
using chat.Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace chat.Infrastructure.Persistence;

internal sealed class TeamInvitationConfiguration : IEntityTypeConfiguration<TeamInvitation>
{
    public void Configure(EntityTypeBuilder<TeamInvitation> entity)
    {
        entity.HasIndex(row => row.TokenHash).IsUnique();
        entity.HasIndex(row => new { row.BusinessId, row.CreatedAt });
        entity.Property(row => row.TokenHash).HasMaxLength(64);
        entity.Property(row => row.Email).HasMaxLength(254);
        entity.Property(row => row.Role).HasConversion<string>().HasMaxLength(12);
        entity.HasOne<Business>().WithMany().HasForeignKey(row => row.BusinessId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<User>().WithMany().HasForeignKey(row => row.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        entity.ToTable(table => table.HasCheckConstraint("CK_TeamInvitations_State",
            "\"Role\" IN ('OWNER','ADMIN','AGENT') AND \"ExpiresAt\" > \"CreatedAt\" AND NOT (\"AcceptedAt\" IS NOT NULL AND \"RevokedAt\" IS NOT NULL)"));
    }
}

internal sealed class ConversationAssignmentConfiguration : IEntityTypeConfiguration<ConversationAssignment>
{
    public void Configure(EntityTypeBuilder<ConversationAssignment> entity)
    {
        entity.HasKey(row => row.ConversationId);
        entity.HasOne<Conversation>().WithMany().HasForeignKey(row => new { row.ConversationId, row.BusinessId })
            .HasPrincipalKey(row => new { row.Id, row.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<BusinessMembership>().WithMany().HasForeignKey(row => new { row.UserId, row.BusinessId })
            .HasPrincipalKey(row => new { row.UserId, row.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<User>().WithMany().HasForeignKey(row => row.UpdatedBy).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(row => new { row.BusinessId, row.UserId });
        entity.ToTable(table => table.HasCheckConstraint("CK_ConversationAssignments_Revision", "\"Revision\" > 0"));
    }
}
