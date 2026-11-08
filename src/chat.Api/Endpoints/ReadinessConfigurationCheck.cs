using chat.Api.DependencyInjection;
using chat.Api.Identity;
using chat.Api.Messaging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace chat.Api.Endpoints;

internal sealed class ReadinessConfigurationCheck(IOptions<JwtSettings> jwt, IOptions<ObjectStorageOptions> storage,
    IOptions<FileScanOptions> scanner, AccountMailCipher mail, IConfiguration configuration, IHostEnvironment environment) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var identityReady = jwt.Value.GetSigningKey() is not null;
        var productionReady = !environment.IsProduction() ||
            (!string.IsNullOrWhiteSpace(storage.Value.ServiceUrl)
             && !string.IsNullOrWhiteSpace(scanner.Value.Host)
             && !string.IsNullOrWhiteSpace(configuration["AccountEmail:Host"]) && mail.IsConfigured);

        return Task.FromResult(identityReady && productionReady
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy());
    }
}
