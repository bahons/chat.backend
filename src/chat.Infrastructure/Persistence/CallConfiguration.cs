using chat.Domain.Calls;
using chat.Domain.Identity;
using chat.Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace chat.Infrastructure.Persistence;

internal sealed class CallConfiguration : IEntityTypeConfiguration<Call>
{
    public void Configure(EntityTypeBuilder<Call> entity)
    {
        entity.Ignore(row => row.IsLive);
        entity.Property(row => row.State).HasConversion<string>().HasMaxLength(16);
        entity.Property(row => row.Kind).HasConversion<string>().HasMaxLength(8);
        entity.Property(row => row.EndedReason).HasMaxLength(32);
        entity.HasOne<Conversation>().WithMany().HasForeignKey(row => new { row.ConversationId, row.ClientUserId, row.BusinessId })
            .HasPrincipalKey(row => new { row.Id, row.ClientUserId, row.BusinessId }).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<User>().WithMany().HasForeignKey(row => row.InitiatorUserId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<User>().WithMany().HasForeignKey(row => row.OperatorUserId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(row => row.ConversationId).IsUnique()
            .HasFilter("\"State\" IN ('RINGING','CONNECTING','ACTIVE')");
        entity.HasIndex(row => new { row.InitiatorUserId, row.ClientCallId }).IsUnique();
        entity.HasIndex(row => new { row.ExpiresAt, row.Id })
            .HasFilter("\"State\" IN ('RINGING','CONNECTING','ACTIVE')");
        entity.ToTable("Calls", table =>
        {
            table.HasCheckConstraint("CK_Calls_State", "\"State\" IN ('RINGING','CONNECTING','ACTIVE','ENDED','MISSED','REJECTED')");
            table.HasCheckConstraint("CK_Calls_Kind", "\"Kind\" IN ('AUDIO','VIDEO')");
            table.HasCheckConstraint("CK_Calls_Participants", "(\"InitiatorIsClient\" AND \"InitiatorUserId\" = \"ClientUserId\") OR (NOT \"InitiatorIsClient\" AND \"OperatorUserId\" IS NOT NULL AND \"InitiatorUserId\" = \"OperatorUserId\")");
            table.HasCheckConstraint("CK_Calls_SelectedOperator", "\"State\" NOT IN ('CONNECTING','ACTIVE') OR (\"OperatorUserId\" IS NOT NULL AND \"OperatorFamilyId\" IS NOT NULL AND \"ClientFamilyId\" IS NOT NULL)");
            table.HasCheckConstraint("CK_Calls_Terminal", "(\"State\" IN ('RINGING','CONNECTING','ACTIVE') AND \"EndedAt\" IS NULL) OR (\"State\" IN ('ENDED','MISSED','REJECTED') AND \"EndedAt\" IS NOT NULL)");
        });
    }
}

