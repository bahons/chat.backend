using chat.Domain.Messaging;
using chat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace chat.Api.Messaging;

internal static class WorkerLease
{
    public static async Task<T?> ClaimAsync<T>(ChatDbContext db, FormattableString select, DateTimeOffset now,
        CancellationToken ct) where T : class, ILeasedWork
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.Set<T>().FromSql(select).ToListAsync(ct);
        var item = rows.SingleOrDefault();
        if (item is not null)
        {
            item.LeaseId = Guid.NewGuid();
            item.AvailableAt = now.AddSeconds(60);
            item.Attempts++;
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return item;
    }

    public static void Finish(ILeasedWork item, DateTimeOffset now, string outcome)
    {
        item.CompletedAt = now;
        item.LeaseId = null;
        item.Outcome = outcome;
    }

    public static void Retry(ILeasedWork item, DateTimeOffset now, int maxDelay = 300)
    {
        item.LeaseId = null;
        item.AvailableAt = now.AddSeconds(Math.Min(maxDelay, Math.Pow(2, Math.Min(item.Attempts, 10))));
    }
}
