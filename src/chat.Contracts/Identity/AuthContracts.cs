using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace chat.Contracts.Identity;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegisterClientRequest(
    [property: Required, EmailAddress, StringLength(254)] string Email,
    [property: Required, StringLength(128, MinimumLength = 15)] string Password,
    [property: Required, StringLength(100)] string DisplayName);

public sealed record LoginRequest(
    [property: Required, EmailAddress, StringLength(254)] string Email,
    [property: Required, StringLength(128)] string Password);

public sealed record RefreshRequest([property: Required, StringLength(43, MinimumLength = 43)] string RefreshToken);
public sealed record VerifyEmailRequest([property: Required, StringLength(43, MinimumLength = 43)] string Token);
public sealed record ForgotPasswordRequest([property: Required, EmailAddress, StringLength(254)] string Email);

public sealed record ResetPasswordRequest(
    [property: Required, StringLength(43, MinimumLength = 43)] string Token,
    [property: Required, StringLength(128, MinimumLength = 15)] string Password);

public sealed record AuthTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string UserKind);
public sealed record MembershipSummary(Guid BusinessId, string Role);
public sealed record MeResponse(Guid Id, string UserKind, string DisplayName, string Email, bool EmailVerified,
    IReadOnlyList<MembershipSummary> BusinessMemberships);
