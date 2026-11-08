using System.ComponentModel.DataAnnotations;
using System.Threading.RateLimiting;
using chat.Application.Identity;
using chat.Domain.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace chat.Api.Identity;

internal static class IdentityServices
{
    public const string RatePolicy = "AccountRequests";

    public static void AddAccountIdentity(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddOptions<IdentityOptions>().BindConfiguration("Identity")
            .Validate(options => options.AccessMinutes is >= 1 and <= 15 && options.RefreshDays is >= 1 and <= 90
                && options.AuthRequestsPerMinute is >= 1 and <= 120
                && options.LoginAttemptsPerAccount is >= 1 and <= 120
                && options.LoginAttemptsPerAddress is >= 1 and <= 120
                && options.LoginWindowSeconds is >= 1 and <= 300
                && options.LoginKeyCapacity is >= 1 and <= 65536, "Invalid identity configuration.")
            .ValidateOnStart();
        services.AddOptions<AccountEmailOptions>().BindConfiguration("AccountEmail")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Host) && options.Port is >= 1 and <= 65535
                && new EmailAddressAttribute().IsValid(options.From)
                && (options.EnableTls || options.Host is "127.0.0.1" or "localhost" or "::1")
                && (options.Username is null || (options.EnableTls && !string.IsNullOrEmpty(options.Password)))
                && options.QueueCapacity is >= 1 and <= 10000
                && (options.QueueKeyBase64 is null || AccountMailCipher.ParseKey(options.QueueKeyBase64) is not null),
                "Invalid account email configuration.")
            .ValidateOnStart();

        services.Configure<PasswordHasherOptions>(options => options.IterationCount = 210_000);
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddSingleton<DummyPassword>();
        services.AddSingleton<LoginAttemptLimiter>();
        services.AddScoped<AccountService>();
        services.AddScoped<AccountCredentials>();
        services.AddSingleton<IAccountEmailSender, SmtpAccountEmailSender>();
        services.AddSingleton<AccountMailCipher>();
        services.AddScoped<AccountMailQueue>();
        services.AddHostedService<AccountEmailDelivery>();
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<IdentityOptions>>((options, identity) =>
            options.AddPolicy(RatePolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = identity.Value.AuthRequestsPerMinute,
                    Window = TimeSpan.FromMinutes(1)
                })));
    }
}
