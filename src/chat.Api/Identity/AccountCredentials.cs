using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using chat.Api.DependencyInjection;
using chat.Api.Middleware;
using chat.Contracts.Identity;
using chat.Domain.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace chat.Api.Identity;

internal sealed class AccountCredentials(IPasswordHasher<User> passwords, IOptions<JwtSettings> jwt,
    IOptions<IdentityOptions> options, AccountMailQueue email, TimeProvider clock)
{
    public static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();
    public static string NewToken() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    public static string TokenHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public User CreateUser(string emailAddress, string password, string displayName, UserKind kind)
    {
        var user = new User
        {
            Email = emailAddress.Trim(),
            NormalizedEmail = NormalizeEmail(emailAddress),
            DisplayName = displayName.Trim(),
            Kind = kind,
            CreatedAt = clock.GetUtcNow()
        };
        user.PasswordHash = passwords.HashPassword(user, password);
        return user;
    }

    public void RequireSigning()
    {
        if (jwt.Value.GetSigningKey() is null)
        {
            throw new ApiFailure(StatusCodes.Status503ServiceUnavailable);
        }
    }

    public string PrepareVerification(User user)
    {
        var token = NewToken();
        user.VerificationHash = TokenHash(token);
        user.VerificationExpiresAt = clock.GetUtcNow().AddHours(24);
        return token;
    }

    public Task CheckMailCapacityAsync(CancellationToken ct) => email.CheckCapacityAsync(ct);

    public void QueueVerification(User user, string token) =>
        email.Enqueue(user.Id, false, token, user.VerificationExpiresAt!.Value);

    public void QueuePasswordReset(Guid userId, string token, DateTimeOffset expiresAt) =>
        email.Enqueue(userId, true, token, expiresAt);

    public AuthTokens Issue(User user, Guid familyId, string refreshToken)
    {
        var settings = jwt.Value;
        var key = settings.GetSigningKey() ?? throw new ApiFailure(StatusCodes.Status503ServiceUnavailable);
        var now = clock.GetUtcNow();
        var expiration = now.AddMinutes(options.Value.AccessMinutes);
        var token = new JwtSecurityToken(settings.Issuer, settings.Audience,
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString("D")),
                new Claim(JwtRegisteredClaimNames.Sid, familyId.ToString("D")),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D")),
                new Claim("userKind", user.Kind.ToString())
            ],
            now.UtcDateTime, expiration.UtcDateTime, new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new AuthTokens(new JwtSecurityTokenHandler().WriteToken(token), refreshToken, expiration, user.Kind.ToString());
    }
}
