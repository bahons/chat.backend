using chat.Domain.Businesses;
using chat.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace chat.Infrastructure.Persistence;

internal sealed class BusinessConfiguration : IEntityTypeConfiguration<Business>
{
    public void Configure(EntityTypeBuilder<Business> entity)
    {
        entity.HasIndex(business => new { business.Country, business.RegistrationNumber }).IsUnique()
            .HasFilter("\"Status\" IN ('VERIFIED', 'SUSPENDED')");
        const string publicFilter = "\"Status\" = 'VERIFIED' AND \"IsActive\"";
        entity.HasIndex(business => new { business.CreatedAt, business.Id }).HasFilter(publicFilter);
        entity.HasIndex(business => business.DisplayName).HasMethod("gin").HasOperators("gin_trgm_ops").HasFilter(publicFilter);
        entity.HasIndex(business => business.Category).HasMethod("gin").HasOperators("gin_trgm_ops").HasFilter(publicFilter);
        entity.Property(business => business.Country).HasMaxLength(2);
        entity.Property(business => business.RegistrationNumber).HasMaxLength(64);
        entity.Property(business => business.Status).HasConversion<string>().HasMaxLength(24);
    }
}

internal sealed class BusinessApplicationConfiguration : IEntityTypeConfiguration<BusinessApplication>
{
    public void Configure(EntityTypeBuilder<BusinessApplication> entity)
    {
        entity.HasIndex(application => application.BusinessId).IsUnique().HasFilter("\"ReviewedAt\" IS NULL");
        entity.HasIndex(application => new { application.SubmittedAt, application.Id });
        entity.HasOne<Business>().WithMany().HasForeignKey(application => application.BusinessId);
        entity.HasOne<User>().WithMany().HasForeignKey(application => application.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<User>().WithMany().HasForeignKey(application => application.ReviewedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ApplicationDraftConfiguration : IEntityTypeConfiguration<ApplicationDraft>
{
    public void Configure(EntityTypeBuilder<ApplicationDraft> entity)
    {
        entity.HasIndex(draft => draft.TokenHash).IsUnique();
        entity.HasIndex(draft => draft.ExpiresAt);
        entity.Property(draft => draft.TokenHash).HasMaxLength(64);
    }
}

internal sealed class VerificationDocumentConfiguration : IEntityTypeConfiguration<VerificationDocument>
{
    public void Configure(EntityTypeBuilder<VerificationDocument> entity)
    {
        entity.HasOne<ApplicationDraft>().WithMany().HasForeignKey(document => document.DraftId);
        entity.HasOne<BusinessApplication>().WithMany().HasForeignKey(document => document.ApplicationId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.Property(document => document.ContentType).HasMaxLength(32);
        entity.Property(document => document.ScanStatus).HasConversion<string>().HasMaxLength(16)
            .HasDefaultValue(VerificationScanStatus.PENDING);
        entity.HasIndex(document => new { document.ScanAfter, document.Id }).HasFilter("\"ScanStatus\" = 'PENDING'");
        entity.ToTable(table => table.HasCheckConstraint("CK_VerificationDocuments_ScanStatus",
            "\"ScanStatus\" IN ('PENDING', 'READY', 'QUARANTINED')"));
    }
}
