using Microsoft.IdentityModel.Tokens;

namespace chat.Api.DependencyInjection;

internal sealed class DatabaseOptions
{
    public string Postgres { get; set; } = "";
}

internal sealed class CorsSettings
{
    public string[] AllowedOrigins { get; set; } = [];
}

internal sealed class RateLimitSettings
{
    public int PermitLimit { get; set; }
    public int WindowSeconds { get; set; }
}

internal sealed class JwtSettings
{
    public string? Issuer { get; set; }
    public string? Audience { get; set; }
    public string? SigningKeyBase64 { get; set; }

    public SymmetricSecurityKey? GetSigningKey()
    {
        if (string.IsNullOrEmpty(SigningKeyBase64))
        {
            return null;
        }

        try
        {
            var key = Convert.FromBase64String(SigningKeyBase64);
            return key.Length >= 32 ? new SymmetricSecurityKey(key) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
