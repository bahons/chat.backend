using System.Globalization;
using System.Net;
using Microsoft.Extensions.Options;

namespace chat.Api.Identity;

internal sealed class LoginAttemptLimiter(IOptions<IdentityOptions> settings, TimeProvider clock)
{
    private readonly object gate = new();
    private readonly Dictionary<string, int> accounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> addresses = new(StringComparer.Ordinal);
    private long windowStarted;
    private bool initialized;

    public int Admit(string email, IPAddress? address)
    {
        var account = AccountCredentials.TokenHash(AccountCredentials.NormalizeEmail(email));
        var network = address?.MapToIPv6().ToString() ?? "unknown";
        var options = settings.Value;
        var window = TimeSpan.FromSeconds(options.LoginWindowSeconds);

        lock (gate)
        {
            var now = clock.GetTimestamp();
            var elapsed = initialized ? clock.GetElapsedTime(windowStarted, now) : window;
            if (elapsed >= window)
            {
                accounts.Clear();
                addresses.Clear();
                windowStarted = now;
                initialized = true;
                elapsed = TimeSpan.Zero;
            }

            var accountCount = accounts.GetValueOrDefault(account);
            var addressCount = addresses.GetValueOrDefault(network);
            if (accountCount >= options.LoginAttemptsPerAccount || addressCount >= options.LoginAttemptsPerAddress
                || (accountCount == 0 && accounts.Count >= options.LoginKeyCapacity)
                || (addressCount == 0 && addresses.Count >= options.LoginKeyCapacity))
            {
                return Math.Max(1, (int)Math.Ceiling((window - elapsed).TotalSeconds));
            }

            accounts[account] = accountCount + 1;
            addresses[network] = addressCount + 1;
            return 0;
        }
    }

    public static void SetRetryAfter(HttpContext context, int seconds) =>
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
}
