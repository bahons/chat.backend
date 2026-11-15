using System.Security.Cryptography;
using System.Text;
using chat.Api.Middleware;
using chat.Contracts.Calls;
using chat.Domain.Calls;
using Microsoft.Extensions.Options;

namespace chat.Api.Calls;

internal sealed class CallOptions
{
    public bool Enabled { get; set; }
    public string? TurnSecret { get; set; }
    public string[] TurnUrls { get; set; } = [];
    public string[] StunUrls { get; set; } = [];
    public int CredentialSeconds { get; set; } = 300;

    public bool IsConfigured => Enabled && Encoding.UTF8.GetByteCount(TurnSecret ?? "") >= 32
        && CredentialSeconds is >= 60 and <= 600 && TurnUrls.Length is > 0 and <= 4 && StunUrls.Length <= 4
        && TurnUrls.All(url => ValidUrl(url, true)) && StunUrls.All(url => ValidUrl(url, false));

    private static bool ValidUrl(string url, bool turn) => url.Length <= 256 &&
        !url.Any(char.IsWhiteSpace) && !url.Contains('@') && !url.Contains('#')
        && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && (turn ? parsed.Scheme is "turn" or "turns" : parsed.Scheme is "stun" or "stuns")
        && parsed.GetComponents(UriComponents.Path, UriFormat.Unescaped).Length > 0;
}

internal sealed class TurnCredentials(IOptions<CallOptions> options, TimeProvider clock)
{
    public void RequireConfigured()
    {
        if (!options.Value.IsConfigured) throw new ApiFailure(StatusCodes.Status503ServiceUnavailable);
    }

    public IceConfiguration Issue(Call call)
    {
        RequireConfigured();
        var expires = clock.GetUtcNow().AddSeconds(options.Value.CredentialSeconds);
        if (expires > call.ExpiresAt) expires = call.ExpiresAt;
        var username = $"{expires.ToUnixTimeSeconds()}:{Convert.ToHexString(RandomNumberGenerator.GetBytes(12))}";
        var credential = Convert.ToBase64String(HMACSHA1.HashData(
            Encoding.UTF8.GetBytes(options.Value.TurnSecret!), Encoding.UTF8.GetBytes(username)));
        var servers = new List<IceServer> { new(options.Value.TurnUrls, username, credential) };
        if (options.Value.StunUrls.Length > 0) servers.Add(new(options.Value.StunUrls, null, null));
        return new IceConfiguration(servers.ToArray(), expires);
    }
}

