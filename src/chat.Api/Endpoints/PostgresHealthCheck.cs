using chat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace chat.Api.Endpoints;

internal sealed class PostgresHealthCheck(ChatDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!await db.Database.CanConnectAsync(cancellationToken))
        {
            return HealthCheckResult.Unhealthy();
        }

        var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken);
        return pending.Any() ? HealthCheckResult.Unhealthy() : HealthCheckResult.Healthy();
    }
}
