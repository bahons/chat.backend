using chat.Api.Endpoints;
using chat.Api.Identity;
using chat.Api.Middleware;
using chat.Contracts;
using chat.Contracts.Businesses;
using chat.Domain.Businesses;
using chat.Domain.Identity;
using chat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace chat.Api.Businesses;

internal sealed class BusinessService(ChatDbContext db, BusinessDrafts drafts, AccountCredentials credentials, TimeProvider clock)
{
    public async Task<BusinessApplicationReceipt> SubmitAsync(SubmitBusinessRequest request, CancellationToken ct)
    {
        RequestValidation.EnsureValid(request.Owner);
        RequestValidation.EnsureValid(request.Business);
        var now = clock.GetUtcNow();
        var owner = credentials.CreateUser(request.Owner.Email, request.Owner.Password, request.Owner.DisplayName,
            UserKind.BUSINESS_MEMBER);
        var verificationToken = credentials.PrepareVerification(owner);
        var business = new Business
        {
            LegalName = request.Business.LegalName.Trim(),
            DisplayName = request.Business.DisplayName.Trim(),
            Country = request.Business.Country.ToUpperInvariant(),
            RegistrationNumber = request.Business.RegistrationNumber.Trim().ToUpperInvariant(),
            Category = request.Business.Category.Trim(),
            Description = request.Business.Description?.Trim(),
            CreatedAt = now
        };
        var application = new BusinessApplication { BusinessId = business.Id, OwnerUserId = owner.Id, SubmittedAt = now };

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var draft = await drafts.LockDraftAsync(AccountCredentials.TokenHash(request.ApplicationDraftToken), ct);
        var ownedIds = await OwnedDocumentsAsync(draft.Id, request.VerificationDocumentIds, ct);
        await credentials.CheckMailCapacityAsync(ct);
        await transaction.CreateSavepointAsync("submit_application", ct);

        db.AddRange(owner, business, application, new BusinessMembership
        {
            BusinessId = business.Id,
            UserId = owner.Id,
            Role = BusinessRole.OWNER,
            CreatedAt = now
        });
        draft.ConsumedAt = now;
        credentials.QueueVerification(owner, verificationToken);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (AccountService.IsEmailConflict(exception))
        {
            // Return the same opaque acknowledgement and consume the draft even if no account can be created.
            await transaction.RollbackToSavepointAsync("submit_application", ct);
            db.ChangeTracker.Clear();
            await db.ApplicationDrafts.Where(row => row.Id == draft.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.ConsumedAt, now), ct);
            await ConsumeDocumentsAsync(draft.Id, [], application.Id, ct);
            await transaction.CommitAsync(ct);
            return new BusinessApplicationReceipt(application.Id, business.Id, BusinessStatus.PENDING_VERIFICATION.ToString());
        }

        await ConsumeDocumentsAsync(draft.Id, ownedIds, application.Id, ct);
        await transaction.CommitAsync(ct);
        return new BusinessApplicationReceipt(application.Id, business.Id, business.Status.ToString());
    }

    public async Task<BusinessApplicationReceipt> ReapplyAsync(Guid actor, Guid id, ReapplyBusinessRequest request,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await db.BusinessMemberships.AnyAsync(row => row.BusinessId == id && row.UserId == actor
                && row.IsActive && row.Role == BusinessRole.OWNER, ct))
        {
            throw new ApiFailure(StatusCodes.Status404NotFound);
        }

        var business = await LockBusinessAsync(id, ct);
        if (!(await db.BusinessMemberships.FromSql($"""
                SELECT * FROM "BusinessMemberships" WHERE "BusinessId" = {id} AND "UserId" = {actor} FOR SHARE
                """).ToListAsync(ct)).Any(row => row.IsActive && row.Role == BusinessRole.OWNER))
        {
            throw new ApiFailure(StatusCodes.Status404NotFound);
        }

        if (business.Status != BusinessStatus.REJECTED)
        {
            throw new ApiFailure(StatusCodes.Status409Conflict);
        }

        var draft = await drafts.LockDraftAsync(AccountCredentials.TokenHash(request.ApplicationDraftToken), ct);
        var documentIds = await OwnedDocumentsAsync(draft.Id, request.VerificationDocumentIds, ct);
        var now = clock.GetUtcNow();
        var application = new BusinessApplication { BusinessId = id, OwnerUserId = actor, SubmittedAt = now };
        business.Status = BusinessStatus.PENDING_VERIFICATION;
        business.IsActive = false;
        draft.ConsumedAt = now;
        db.AddRange(application, new AuditLog { ActorId = actor, EntityId = application.Id, Action = "REAPPLY", CreatedAt = now });
        await db.SaveChangesAsync(ct);
        await ConsumeDocumentsAsync(draft.Id, documentIds, application.Id, ct);
        await transaction.CommitAsync(ct);
        return new BusinessApplicationReceipt(application.Id, id, business.Status.ToString());
    }

    private async Task<Guid[]> OwnedDocumentsAsync(Guid draftId, Guid[] ids, CancellationToken ct)
    {
        if (ids.Distinct().Count() != ids.Length)
        {
            throw new ApiFailure(StatusCodes.Status400BadRequest);
        }

        var owned = await db.VerificationDocuments.Where(document => document.DraftId == draftId
                && document.ApplicationId == null && ids.Contains(document.Id))
            .Select(document => document.Id).ToArrayAsync(ct);
        return owned.Length == ids.Length ? owned : throw new ApiFailure(StatusCodes.Status404NotFound);
    }

    private async Task ConsumeDocumentsAsync(Guid draftId, Guid[] ids, Guid applicationId, CancellationToken ct)
    {
        if (ids.Length > 0)
        {
            await db.VerificationDocuments.Where(document => ids.Contains(document.Id)).ExecuteUpdateAsync(
                update => update.SetProperty(document => document.ApplicationId, applicationId), ct);
        }

        await db.VerificationDocuments.Where(document => document.DraftId == draftId && document.ApplicationId == null)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<BusinessProfile> GetProfileAsync(Guid actor, Guid? businessId, CancellationToken ct)
    {
        var id = await GetManagedBusinessIdAsync(actor, businessId, ct);
        var business = await db.Businesses.AsNoTracking().SingleAsync(business => business.Id == id, ct);
        return Profile(business);
    }

    public async Task<BusinessProfile> UpdateProfileAsync(Guid actor, Guid? businessId, UpdateBusinessRequest request,
        CancellationToken ct)
    {
        if (request.DisplayName is not null && string.IsNullOrWhiteSpace(request.DisplayName)
            || request.Category is not null && string.IsNullOrWhiteSpace(request.Category))
        {
            throw new ApiFailure(StatusCodes.Status400BadRequest);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var id = await GetManagedBusinessIdAsync(actor, businessId, ct);
        var business = await LockBusinessAsync(id, ct);
        await GetManagedBusinessIdAsync(actor, id, ct);
        RequireActive(business);
        business.DisplayName = request.DisplayName?.Trim() ?? business.DisplayName;
        business.Category = request.Category?.Trim() ?? business.Category;
        business.Description = request.Description?.Trim() ?? business.Description;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Profile(business);
    }

    public async Task<PageResponse<BusinessApplicationSummary>> ListAsync(string? status, CursorPage page, CancellationToken ct)
    {
        var query = from application in db.BusinessApplications.AsNoTracking()
                    join business in db.Businesses.AsNoTracking() on application.BusinessId equals business.Id
                    select new
                    {
                        application.Id,
                        application.BusinessId,
                        business.DisplayName,
                        application.SubmittedAt,
                        Status = application.Decision == "REJECT" ? BusinessStatus.REJECTED : business.Status
                    };
        if (status is not null)
        {
            if (!Enum.TryParse<BusinessStatus>(status, out var parsed) || parsed.ToString() != status)
            {
                throw new ApiFailure(StatusCodes.Status400BadRequest);
            }

            query = query.Where(row => row.Status == parsed);
        }

        if (page.Before is { } before)
        {
            query = query.Where(row => EF.Functions.LessThan(ValueTuple.Create(row.SubmittedAt, row.Id),
                ValueTuple.Create(before, page.BeforeId)));
        }

        var rows = await query.OrderByDescending(row => row.SubmittedAt).ThenByDescending(row => row.Id)
            .Take(page.Limit + 1).ToListAsync(ct);
        return page.MakePage(rows, row => new BusinessApplicationSummary(row.Id, row.BusinessId, row.DisplayName,
            row.Status.ToString(), row.SubmittedAt), row => row.SubmittedAt, row => row.Id);
    }

    public async Task<BusinessApplicationDetails> GetApplicationAsync(Guid id, CancellationToken ct)
    {
        var row = await (from application in db.BusinessApplications.AsNoTracking()
                         join business in db.Businesses.AsNoTracking() on application.BusinessId equals business.Id
                         where application.Id == id
                         select new { Application = application, Business = business })
            .SingleOrDefaultAsync(ct) ?? throw new ApiFailure(StatusCodes.Status404NotFound);
        var documents = await db.VerificationDocuments.AsNoTracking().Where(document => document.ApplicationId == id)
            .OrderBy(document => document.Id).Select(document => new VerificationDocumentInfo(document.Id, document.ContentType, document.Size, document.ScanStatus.ToString()))
            .ToArrayAsync(ct);
        return new BusinessApplicationDetails(row.Application.Id, row.Business.Id, row.Business.LegalName, row.Business.DisplayName,
            row.Business.RegistrationNumber, row.Business.Country, row.Business.Category,
            (row.Application.Decision == "REJECT" ? BusinessStatus.REJECTED : row.Business.Status).ToString(),
            row.Application.SubmittedAt, row.Application.ReviewedAt, row.Application.Reason, documents);
    }

    public async Task<VerificationDocument> GetDocumentAsync(Guid id, Guid documentId, CancellationToken ct) =>
        await db.VerificationDocuments.AsNoTracking().SingleOrDefaultAsync(document => document.Id == documentId
            && document.ApplicationId == id && document.ScanStatus == VerificationScanStatus.READY, ct) ?? throw new ApiFailure(StatusCodes.Status404NotFound);

    public async Task<BusinessApplicationReceipt> DecideAsync(Guid actor, Guid id, DecisionRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var application = await db.BusinessApplications.SingleOrDefaultAsync(application => application.Id == id, ct)
            ?? throw new ApiFailure(StatusCodes.Status404NotFound);
        var business = await LockBusinessAsync(application.BusinessId, ct);
        await db.Entry(application).ReloadAsync(ct);
        if (business.Status != BusinessStatus.PENDING_VERIFICATION || application.ReviewedAt is not null)
        {
            throw new ApiFailure(StatusCodes.Status409Conflict);
        }

        var approved = request.Decision == "APPROVE";
        if (approved)
        {
            if (!await db.Users.AnyAsync(user => user.Id == application.OwnerUserId && user.IsActive
                    && user.EmailVerifiedAt != null, ct))
            {
                throw new ApiFailure(StatusCodes.Status409Conflict);
            }

            var documents = db.VerificationDocuments.Where(document => document.ApplicationId == id);
            if (!await documents.AnyAsync(ct) ||
                await documents.AnyAsync(document => document.ScanStatus != VerificationScanStatus.READY, ct))
            {
                throw new ApiFailure(StatusCodes.Status409Conflict, "MEDIA_NOT_READY", "Verification documents are unavailable.");
            }
        }

        var now = clock.GetUtcNow();
        application.Decision = request.Decision;
        application.ReviewedByUserId = actor;
        application.ReviewedAt = now;
        application.Reason = request.Reason?.Trim();
        business.Status = approved ? BusinessStatus.VERIFIED : BusinessStatus.REJECTED;
        business.IsActive = approved;
        db.AuditLogs.Add(new AuditLog { ActorId = actor, EntityId = id, Action = request.Decision, CreatedAt = now, Reason = application.Reason });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_Businesses_Country_RegistrationNumber" })
        {
            throw new ApiFailure(StatusCodes.Status409Conflict);
        }
        await transaction.CommitAsync(ct);
        return new BusinessApplicationReceipt(id, business.Id, business.Status.ToString());
    }

    public async Task SuspendAsync(Guid actor, Guid id, string reason, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var business = await LockBusinessAsync(id, ct);
        if (business.Status != BusinessStatus.VERIFIED)
        {
            throw new ApiFailure(StatusCodes.Status409Conflict);
        }

        business.Status = BusinessStatus.SUSPENDED;
        business.IsActive = false;
        db.AuditLogs.Add(new AuditLog { ActorId = actor, EntityId = id, Action = "SUSPEND", CreatedAt = clock.GetUtcNow(), Reason = reason.Trim() });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    internal async Task<Business> LockBusinessAsync(Guid id, CancellationToken ct)
    {
        var rows = await db.Businesses.FromSql($"SELECT * FROM \"Businesses\" WHERE \"Id\" = {id} FOR UPDATE").ToListAsync(ct);
        return rows.SingleOrDefault() ?? throw new ApiFailure(StatusCodes.Status404NotFound);
    }

    internal static void RequireActive(Business business)
    {
        if (business.Status != BusinessStatus.VERIFIED || !business.IsActive)
        {
            var code = business.Status == BusinessStatus.SUSPENDED ? "SUSPENDED" : "VERIFICATION_REQUIRED";
            throw new ApiFailure(StatusCodes.Status403Forbidden, code, "Business access is unavailable.");
        }
    }

    private async Task<Guid> GetManagedBusinessIdAsync(Guid actor, Guid? id, CancellationToken ct)
    {
        var memberships = await db.BusinessMemberships.Where(membership => membership.UserId == actor && membership.IsActive
                && (membership.Role == BusinessRole.OWNER || membership.Role == BusinessRole.ADMIN)
                && (id == null || membership.BusinessId == id))
            .Select(membership => membership.BusinessId).Take(2).ToArrayAsync(ct);
        return memberships.Length switch
        {
            1 => memberships[0],
            0 => throw new ApiFailure(StatusCodes.Status404NotFound),
            _ => throw new ApiFailure(StatusCodes.Status400BadRequest)
        };
    }

    private static BusinessProfile Profile(Business business) =>
        new(business.Id, business.DisplayName, business.Category, business.Description, business.Status.ToString(), business.IsActive);
}
