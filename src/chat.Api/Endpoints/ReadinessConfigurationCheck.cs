using chat.Api.DependencyInjection;
using chat.Api.Calls;
using chat.Api.Identity;
using chat.Api.Messaging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace chat.Api.Endpoints;

internal sealed class ReadinessConfigurationCheck(IOptions<JwtSettings> jwt, IOptions<ObjectStorageOptions> storage,
    IOptions<FileScanOptions> scanner, AccountMailCipher mail, IConfiguration configuration, IHostEnvironment environment,
    IOptions<CallOptions> calls) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var identityReady = jwt.Value.GetSigningKey() is not null;
        var productionReady = !environment.IsProduction() ||
            (!string.IsNullOrWhiteSpace(storage.Value.ServiceUrl)
             && !string.IsNullOrWhiteSpace(scanner.Value.Host)
             && (!configuration.GetValue("AccountEmail:Enabled", true)
                 || (!string.IsNullOrWhiteSpace(configuration["AccountEmail:Host"]) && mail.IsConfigured)));

        return Task.FromResult(identityReady && productionReady && (!calls.Value.Enabled || calls.Value.IsConfigured)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy());
    }
}
