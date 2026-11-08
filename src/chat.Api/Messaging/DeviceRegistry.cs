using chat.Api.Identity;
using chat.Api.Middleware;
using chat.Contracts.Messaging;
using chat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace chat.Api.Messaging;

internal sealed class DeviceRegistry(ChatDbContext db, TimeProvider clock)
{
    public static readonly TimeSpan InactivityLimit = TimeSpan.FromDays(90);

    public async Task<RegisteredDevice> RegisterAsync(Guid userId, Guid familyId, string token, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = await RequireCurrentSessionAsync(userId, familyId, ct);
        var id = Guid.NewGuid();
        var result = await db.Database.SqlQuery<Guid>($"""
            INSERT INTO "DeviceRegistrations" ("Id", "UserId", "FamilyId", "PushToken", "UpdatedAt")
            VALUES ({id}, {userId}, {familyId}, {token}, {now})
            ON CONFLICT ("PushToken") DO UPDATE
            SET "UserId" = EXCLUDED."UserId", "FamilyId" = EXCLUDED."FamilyId",
                "UpdatedAt" = EXCLUDED."UpdatedAt", "RevokedAt" = NULL, "ReleasedAt" = NULL
            WHERE "DeviceRegistrations"."UserId" = EXCLUDED."UserId" OR "DeviceRegistrations"."ReleasedAt" IS NOT NULL
            RETURNING "Id" AS "Value"
            """).ToListAsync(ct);
        if (result.Count == 0)
        {
            throw new ApiFailure(StatusCodes.Status409Conflict);
        }

        await transaction.CommitAsync(ct);
        return new RegisteredDevice(result.Single());
    }

    public async Task ReleaseAsync(Guid id, Guid userId, Guid familyId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = await RequireCurrentSessionAsync(userId, familyId, ct);
        var count = await db.DeviceRegistrations.Where(device => device.Id == id && device.UserId == userId)
            .ExecuteUpdateAsync(update => update.SetProperty(device => device.RevokedAt, now)
                .SetProperty(device => device.ReleasedAt, now), ct);
        if (count == 0)
        {
            throw new ApiFailure(StatusCodes.Status404NotFound);
        }

        await transaction.CommitAsync(ct);
    }

    private async Task<DateTimeOffset> RequireCurrentSessionAsync(Guid userId, Guid familyId, CancellationToken ct)
    {
        var rows = await db.Users.FromSql($"SELECT * FROM \"Users\" WHERE \"Id\" = {userId} FOR SHARE")
            .AsNoTracking().ToListAsync(ct);
        var now = clock.GetUtcNow();
        if (rows.SingleOrDefault() is not { IsActive: true, EmailVerifiedAt: not null }
            || !await AuthPrincipal.ActiveSessions(db, now).AnyAsync(session => session.UserId == userId
                && session.FamilyId == familyId, ct))
        {
            throw new ApiFailure(StatusCodes.Status401Unauthorized);
        }

        return now;
    }
}
