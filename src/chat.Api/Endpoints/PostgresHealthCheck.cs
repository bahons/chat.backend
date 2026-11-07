using chat.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace chat.Api.Endpoints;

internal sealed class PostgresHealthCheck(ChatDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy();
    }
}
