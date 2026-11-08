using chat.Api.Middleware;
using chat.Contracts.Identity;
using chat.Domain.Identity;
using chat.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;

namespace chat.Api.Identity;

internal sealed class AccountService(ChatDbContext db, AccountCredentials credentials, IPasswordHasher<User> passwords,
    DummyPassword dummy, TimeProvider clock, IOptions<IdentityOptions> options)
{
    public async Task RegisterClientAsync(RegisterClientRequest request, CancellationToken cancellationToken)
    {
        var user = credentials.CreateUser(request.Email, request.Password, request.DisplayName, UserKind.CLIENT);
        var verificationToken = credentials.PrepareVerification(user);
        await using var transaction = await BeginIdentityTransactionAsync(cancellationToken);
        await credentials.CheckMailCapacityAsync(cancellationToken);
        db.Users.Add(user);
        credentials.QueueVerification(user, verificationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsEmailConflict(exception))
        {
            return;
        }

    }

    public async Task<AuthTokens> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        credentials.RequireSigning();
        var normalizedEmail = AccountCredentials.NormalizeEmail(request.Email);
        var snapshot = await db.Users.AsNoTracking().SingleOrDefaultAsync(
            user => user.NormalizedEmail == normalizedEmail, cancellationToken);
        var result = passwords.VerifyHashedPassword(snapshot ?? dummy.User,
            snapshot?.PasswordHash ?? dummy.User.PasswordHash, request.Password);
        if (snapshot is not { IsActive: true } || result == PasswordVerificationResult.Failed)
        {
            throw Unauthorized();
        }

        var rehashed = result == PasswordVerificationResult.SuccessRehashNeeded
            ? passwords.HashPassword(snapshot, request.Password)
            : null;
        await using var transaction = await BeginIdentityTransactionAsync(cancellationToken);
        var user = await LockUserAsync(snapshot.Id, cancellationToken);
        if (user is not { IsActive: true } || user.NormalizedEmail != normalizedEmail
            || user.PasswordHash != snapshot.PasswordHash)
        {
            throw Unauthorized();
        }

        if (user.EmailVerifiedAt is null)
        {
            if (user.VerificationExpiresAt is null || user.VerificationExpiresAt <= clock.GetUtcNow().AddHours(24).AddMinutes(-1))
            {
                var verificationToken = credentials.PrepareVerification(user);
                await credentials.CheckMailCapacityAsync(cancellationToken);
                credentials.QueueVerification(user, verificationToken);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            throw new ApiFailure(StatusCodes.Status403Forbidden, "VERIFICATION_REQUIRED", "Verify your email address.");
        }

        if (rehashed is not null)
        {
            user.PasswordHash = rehashed;
        }

        var tokens = CreateSession(user, Guid.NewGuid(), clock.GetUtcNow().AddDays(options.Value.RefreshDays));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return tokens;
    }

    public async Task<AuthTokens> RefreshAsync(string token, CancellationToken cancellationToken)
    {
        credentials.RequireSigning();
        var hash = AccountCredentials.TokenHash(token);
        var userId = await db.RefreshSessions.Where(session => session.TokenHash == hash)
            .Select(session => (Guid?)session.UserId).SingleOrDefaultAsync(cancellationToken);
        if (!userId.HasValue)
        {
            throw Unauthorized();
        }

        await using var transaction = await BeginIdentityTransactionAsync(cancellationToken);
        var user = await LockUserAsync(userId.Value, cancellationToken);
        var previous = await db.RefreshSessions.SingleOrDefaultAsync(session => session.TokenHash == hash, cancellationToken);
        var now = clock.GetUtcNow();
        if (user is not { IsActive: true, EmailVerifiedAt: not null } || previous is null
            || previous.RevokedAt is not null || previous.ExpiresAt <= now)
        {
            throw Unauthorized();
        }

        if (previous.ConsumedAt is not null)
        {
            await RevokeAsync(user.Id, previous.FamilyId, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw Unauthorized();
        }

        previous.ConsumedAt = now;
        var tokens = CreateSession(user, previous.FamilyId, previous.ExpiresAt);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return tokens;
    }

    public async Task LogoutAsync(Guid userId, Guid familyId, CancellationToken cancellationToken)
    {
        await using var transaction = await BeginIdentityTransactionAsync(cancellationToken);
        await LockUserAsync(userId, cancellationToken);
        await RevokeAsync(userId, familyId, clock.GetUtcNow(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task VerifyAsync(string token, CancellationToken cancellationToken)
    {
        var hash = AccountCredentials.TokenHash(token);
        var now = clock.GetUtcNow();
        var count = await db.Users.Where(user => user.IsActive && user.EmailVerifiedAt == null
                && user.VerificationHash == hash && user.VerificationExpiresAt > now)
            .ExecuteUpdateAsync(update => update.SetProperty(user => user.EmailVerifiedAt, now)
                .SetProperty(user => user.VerificationHash, (string?)null)
                .SetProperty(user => user.VerificationExpiresAt, (DateTimeOffset?)null), cancellationToken);
        if (count == 0)
        {
            throw new ApiFailure(StatusCodes.Status400BadRequest);
        }
    }

    public async Task ForgotAsync(string email, CancellationToken cancellationToken)
    {
        var normalizedEmail = AccountCredentials.NormalizeEmail(email);
        var token = AccountCredentials.NewToken();
        var hash = AccountCredentials.TokenHash(token);
        var now = clock.GetUtcNow();
        var expiresAt = now.AddMinutes(20);
        var resendAfter = now.AddMinutes(19);
        await using var transaction = await BeginIdentityTransactionAsync(cancellationToken);
        await credentials.CheckMailCapacityAsync(cancellationToken);
        var recipients = await db.Database.SqlQuery<Guid>($"""
            UPDATE "Users" SET "ResetHash" = {hash}, "ResetExpiresAt" = {expiresAt}
            WHERE "NormalizedEmail" = {normalizedEmail} AND "IsActive" AND "EmailVerifiedAt" IS NOT NULL
                AND ("ResetExpiresAt" IS NULL OR "ResetExpiresAt" <= {resendAfter})
            RETURNING "Id" AS "Value"
            """).ToListAsync(cancellationToken);
        if (recipients.Count > 0)
        {
            credentials.QueuePasswordReset(recipients.Single(), token, expiresAt);
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ResetAsync(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        var hash = AccountCredentials.TokenHash(request.Token);
        var snapshot = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.ResetHash == hash, cancellationToken);
        if (snapshot is not { IsActive: true } || !(snapshot.ResetExpiresAt > clock.GetUtcNow()))
        {
            throw new ApiFailure(StatusCodes.Status400BadRequest);
        }

        var rehashed = passwords.HashPassword(snapshot, request.Password);
        await using var transaction = await BeginIdentityTransactionAsync(cancellationToken);
        var user = await LockUserAsync(snapshot.Id, cancellationToken);
        var now = clock.GetUtcNow();
        if (user is not { IsActive: true } || user.ResetHash != hash || !(user.ResetExpiresAt > now))
        {
            throw new ApiFailure(StatusCodes.Status400BadRequest);
        }

        user.PasswordHash = rehashed;
        user.ResetHash = null;
        user.ResetExpiresAt = null;
        await RevokeAsync(user.Id, null, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<MeResponse> MeAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == userId && user.IsActive,
            cancellationToken) ?? throw Unauthorized();
        var memberships = await db.BusinessMemberships.AsNoTracking()
            .Where(membership => membership.UserId == userId && membership.IsActive)
            .OrderBy(membership => membership.BusinessId)
            .Select(membership => new MembershipSummary(membership.BusinessId, membership.Role.ToString()))
            .ToArrayAsync(cancellationToken);
        return new MeResponse(user.Id, user.Kind.ToString(), user.DisplayName, user.Email,
            user.EmailVerifiedAt is not null, memberships);
    }

    internal static bool IsEmailConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_Users_NormalizedEmail"
        };

    private async Task<IDbContextTransaction> BeginIdentityTransactionAsync(CancellationToken cancellationToken)
    {
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await db.Database.ExecuteSqlRawAsync("""
                SET LOCAL lock_timeout = '2s';
                SET LOCAL statement_timeout = '5s';
                SET LOCAL idle_in_transaction_session_timeout = '10s';
                """, cancellationToken);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    private async Task<User?> LockUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var users = await db.Users.FromSql($"SELECT * FROM \"Users\" WHERE \"Id\" = {userId} FOR UPDATE")
                .ToListAsync(cancellationToken);
            return users.SingleOrDefault();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
            && exception.GetBaseException() is PostgresException
            { SqlState: PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.QueryCanceled })
        {
            throw new ApiFailure(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private Task<int> RevokeAsync(Guid userId, Guid? familyId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.RefreshSessions.Where(session => session.UserId == userId && session.RevokedAt == null
                && (familyId == null || session.FamilyId == familyId))
            .ExecuteUpdateAsync(update => update.SetProperty(session => session.RevokedAt, now), cancellationToken);

    private AuthTokens CreateSession(User user, Guid familyId, DateTimeOffset expiresAt)
    {
        var token = AccountCredentials.NewToken();
        db.RefreshSessions.Add(new RefreshSession
        {
            UserId = user.Id,
            FamilyId = familyId,
            TokenHash = AccountCredentials.TokenHash(token),
            ExpiresAt = expiresAt
        });
        return credentials.Issue(user, familyId, token);
    }

    private static ApiFailure Unauthorized() => new(StatusCodes.Status401Unauthorized);
}

internal sealed class DummyPassword
{
    public DummyPassword(IPasswordHasher<User> passwords)
    {
        User = new User { Email = "", NormalizedEmail = "", DisplayName = "" };
        User.PasswordHash = passwords.HashPassword(User, AccountCredentials.NewToken());
    }

    public User User { get; }
}
