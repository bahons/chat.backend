using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using chat.Contracts.Identity;
using Xunit;

namespace chat.IntegrationTests;

public sealed class IdentityTests(BackendFixture backend) : IClassFixture<BackendFixture>
{
    private const string Password = "integration-test-password";
    private const string NewPassword = "updated-integration-test-password";

    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task AccountLifecycleRejectsEscalationReplayAndRevokedAccess()
    {
        using var client = backend.Host.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.test";
        using var escalation = await client.PostAsJsonAsync("/v1/auth/client/register",
            new { email, password = Password, displayName = "Client", userKind = "PLATFORM_ADMIN" });
        Assert.Equal(HttpStatusCode.BadRequest, escalation.StatusCode);

        using var registration = await client.PostAsJsonAsync("/v1/auth/client/register",
            new RegisterClientRequest(email, Password, "Client"));
        Assert.Equal(HttpStatusCode.Accepted, registration.StatusCode);
        var verification = await backend.ReadCodeAsync("chat email verification");
        using var verify = await client.PostAsJsonAsync("/v1/auth/email/verify", new VerifyEmailRequest(verification));
        Assert.Equal(HttpStatusCode.NoContent, verify.StatusCode);
        using var used = await client.PostAsJsonAsync("/v1/auth/email/verify", new VerifyEmailRequest(verification));
        Assert.Equal(HttpStatusCode.BadRequest, used.StatusCode);

        var original = await LoginAsync(client, email, Password);
        Assert.Equal("CLIENT", original.UserKind);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", original.AccessToken);
        var me = await client.GetFromJsonAsync<MeResponse>("/v1/me");
        Assert.Equal("CLIENT", me?.UserKind);
        Assert.Empty(me!.BusinessMemberships);
        await CheckWebSocketAsync(original.AccessToken);
        client.DefaultRequestHeaders.Authorization = null;

        using var rotation = await client.PostAsJsonAsync("/v1/auth/refresh", new RefreshRequest(original.RefreshToken));
        var rotated = await rotation.Content.ReadFromJsonAsync<AuthTokens>();
        Assert.Equal(HttpStatusCode.OK, rotation.StatusCode);
        Assert.NotNull(rotated);
        Assert.NotEqual(original.RefreshToken, rotated.RefreshToken);
        using var replay = await client.PostAsJsonAsync("/v1/auth/refresh", new RefreshRequest(original.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        using var revoked = await client.PostAsJsonAsync("/v1/auth/refresh", new RefreshRequest(rotated.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rotated.AccessToken);
        using var revokedAccess = await client.GetAsync("/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, revokedAccess.StatusCode);
        client.DefaultRequestHeaders.Authorization = null;

        var beforeReset = await LoginAsync(client, email, Password);
        using var unknown = await client.PostAsJsonAsync("/v1/auth/password/forgot", new ForgotPasswordRequest("unknown@example.test"));
        using var known = await client.PostAsJsonAsync("/v1/auth/password/forgot", new ForgotPasswordRequest(email));
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, known.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await known.Content.ReadAsStringAsync());
        var reset = await backend.ReadCodeAsync("chat password reset");
        using var resetResponse = await client.PostAsJsonAsync("/v1/auth/password/reset", new ResetPasswordRequest(reset, NewPassword));
        Assert.Equal(HttpStatusCode.NoContent, resetResponse.StatusCode);
        using var oldRefresh = await client.PostAsJsonAsync("/v1/auth/refresh", new RefreshRequest(beforeReset.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, oldRefresh.StatusCode);

        var newSession = await LoginAsync(client, email, NewPassword);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newSession.AccessToken);
        using var logout = await client.PostAsync("/v1/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var loggedOut = await client.GetAsync("/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, loggedOut.StatusCode);
    }

    private static async Task<AuthTokens> LoginAsync(HttpClient client, string email, string password)
    {
        using var response = await client.PostAsJsonAsync("/v1/auth/login", new LoginRequest(email, password));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthTokens>())!;
    }

    private async Task CheckWebSocketAsync(string token)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await backend.Host.Server.CreateWebSocketClient().ConnectAsync(
            new Uri($"wss://localhost/hubs/messaging?access_token={token}"), timeout.Token);
        var handshake = "{\"protocol\":\"json\",\"version\":1}\u001e"u8.ToArray();
        await socket.SendAsync(handshake.AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        var buffer = new byte[256];
        var frame = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
        Assert.Equal(WebSocketMessageType.Text, frame.MessageType);
        Assert.Equal("{}\u001e", Encoding.UTF8.GetString(buffer, 0, frame.Count));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
    }
}
