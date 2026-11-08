using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using chat.Domain.Identity;
using chat.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

namespace chat.Api.Identity;

internal static class AuthPrincipal
{
    public const string ClientPolicy = "Client";
    public const string BusinessPolicy = "BusinessMember";
    public const string ParticipantPolicy = "ConversationParticipant";
    public const string AdminPolicy = "PlatformAdmin";
    public static UserKind Kind(this ClaimsPrincipal principal) => Enum.Parse<UserKind>(principal.FindFirstValue("userKind")!);
    public static Guid UserId(this ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
    public static Guid FamilyId(this ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue(JwtRegisteredClaimNames.Sid)!);

    public static IQueryable<RefreshSession> ActiveSessions(ChatDbContext db, DateTimeOffset now) =>
        db.RefreshSessions.Where(session => session.ExpiresAt > now && session.ConsumedAt == null && session.RevokedAt == null);

    public static async Task ValidateAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        if (!Guid.TryParse(principal?.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId)
            || !Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sid), out var familyId)
            || principal.Identity is not ClaimsIdentity identity)
        {
            context.Fail("Invalid session.");
            return;
        }

        var db = context.HttpContext.RequestServices.GetRequiredService<ChatDbContext>();
        var now = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var kind = await db.Users.Where(user => user.Id == userId && user.IsActive && user.EmailVerifiedAt != null
                && ActiveSessions(db, now).Any(session => session.UserId == userId && session.FamilyId == familyId))
            .Select(user => (UserKind?)user.Kind).SingleOrDefaultAsync(context.HttpContext.RequestAborted);
        if (kind is null)
        {
            context.Fail("Invalid session.");
            return;
        }

        foreach (var claim in identity.FindAll("userKind").ToArray())
        {
            identity.RemoveClaim(claim);
        }

        identity.AddClaim(new Claim("userKind", kind.Value.ToString()));
    }
}
