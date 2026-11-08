using System.Threading.RateLimiting;
using chat.Api.Endpoints;
using chat.Api.Identity;
using chat.Api.Middleware;
using chat.Domain.Identity;
using chat.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace chat.Api.DependencyInjection;

internal static class BootstrapServices
{
    public const string SignedInPolicy = "SignedIn";
    public const string MessagingPath = "/hubs/messaging";

    public static void AddBootstrap(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddExceptionHandler(options =>
        {
            options.ExceptionHandler = context => ApiErrors.WriteAsync(context, StatusCodes.Status500InternalServerError);
            options.SuppressDiagnosticsCallback = _ => true;
        });

        services.AddOptions<DatabaseOptions>().BindConfiguration("ConnectionStrings")
            .Validate(options => IsPostgresConnectionString(options.Postgres), "ConnectionStrings:Postgres is invalid.")
            .ValidateOnStart();
        services.AddDbContext<ChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.Postgres));
        services.AddHealthChecks().AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"],
            timeout: TimeSpan.FromSeconds(5))
            .AddCheck<ReadinessConfigurationCheck>("configuration", tags: ["ready"]);

        services.AddOptions<CorsSettings>().BindConfiguration("Cors")
            .Validate(options => options.AllowedOrigins.All(IsOrigin), "Cors:AllowedOrigins must contain exact HTTP(S) origins.")
            .ValidateOnStart();
        services.AddCors(options => options.AddDefaultPolicy(policy =>
        {
            var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
            policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()
                .WithExposedHeaders(RequestContextMiddleware.HeaderName, "Retry-After");
        }));

        services.AddOptions<RateLimitSettings>().BindConfiguration("RateLimiting")
            .Validate(options => options.PermitLimit > 0 && options.WindowSeconds > 0,
                "RateLimiting limits must be positive.")
            .ValidateOnStart();
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<RateLimitSettings>>((options, settings) =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay))
                {
                    LoginAttemptLimiter.SetRetryAfter(context.HttpContext, Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds)));
                }

                return ValueTask.CompletedTask;
            };
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = settings.Value.PermitLimit,
                        Window = TimeSpan.FromSeconds(settings.Value.WindowSeconds)
                    }));
        });

        services.AddOptions<JwtSettings>().BindConfiguration("Jwt")
            .Validate(options => string.IsNullOrEmpty(options.SigningKeyBase64)
                || (!string.IsNullOrWhiteSpace(options.Issuer)
                    && !string.IsNullOrWhiteSpace(options.Audience)
                    && options.GetSigningKey() is not null),
                "Jwt requires an issuer, audience and a base64 signing key of at least 32 bytes.")
            .ValidateOnStart();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtSettings>>((options, settings) => ConfigureBearer(options, settings.Value));

        services.AddSignalR();

        var signedIn = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireAssertion(context => !string.IsNullOrWhiteSpace(context.User.FindFirst("sub")?.Value))
            .Build();
        services.AddAuthorizationBuilder()
            .SetDefaultPolicy(signedIn)
            .AddPolicy(SignedInPolicy, signedIn)
            .AddPolicy(AuthPrincipal.ClientPolicy, policy => policy.Combine(signedIn).RequireClaim("userKind", nameof(UserKind.CLIENT)))
            .AddPolicy(AuthPrincipal.BusinessPolicy, policy => policy.Combine(signedIn).RequireClaim("userKind", nameof(UserKind.BUSINESS_MEMBER)))
            .AddPolicy(AuthPrincipal.ParticipantPolicy, policy => policy.Combine(signedIn)
                .RequireClaim("userKind", nameof(UserKind.CLIENT), nameof(UserKind.BUSINESS_MEMBER)))
            .AddPolicy(AuthPrincipal.AdminPolicy, policy => policy.Combine(signedIn).RequireClaim("userKind", nameof(UserKind.PLATFORM_ADMIN)));
    }

    private static void ConfigureBearer(JwtBearerOptions options, JwtSettings settings)
    {
        options.MapInboundClaims = false;
        options.IncludeErrorDetails = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = settings.Issuer,
            ValidateAudience = true,
            ValidAudience = settings.Audience,
            IgnoreTrailingSlashWhenValidatingAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = settings.GetSigningKey(),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.Zero
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = AuthPrincipal.ValidateAsync,
            OnMessageReceived = context =>
            {
                var request = context.Request;
                if (request.IsHttps && HttpMethods.IsGet(request.Method) && request.Path == MessagingPath
                    && !request.Headers.ContainsKey("Authorization")
                    && request.Query.TryGetValue("access_token", out var tokens) && tokens.Count == 1)
                {
                    context.Token = tokens[0];
                }

                return Task.CompletedTask;
            }
        };
    }

    private static bool IsOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.Equals(origin, uri.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal);

    private static bool IsPostgresConnectionString(string value)
    {
        try
        {
            var settings = new NpgsqlConnectionStringBuilder(value);
            return !string.IsNullOrWhiteSpace(settings.Host) && !string.IsNullOrWhiteSpace(settings.Database);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
