using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using chat.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace chat.IntegrationTests;

public sealed class BootstrapTests : IDisposable
{
    private const string NegotiatePath = "/hubs/messaging/negotiate?negotiateVersion=1";
    private readonly WebApplicationFactory<Program> factory = new();

    [Fact]
    public async Task LivenessWorksWhileDatabaseIsUnavailable()
    {
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Postgres",
            "Host=127.0.0.1;Port=1;Database=chat;Username=chat;Timeout=1"));
        using var client = host.CreateClient();
        using var live = await client.GetAsync("/health/live");
        using var ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("healthy", (await live.Content.ReadFromJsonAsync<HealthResponse>())?.Status);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        await AssertErrorAsync(ready, "INTERNAL_ERROR");
    }

    [Fact]
    public async Task UnknownRouteReturnsVersionedErrorAndRequestId()
    {
        using var client = factory.CreateClient();
        var requestId = Guid.NewGuid().ToString("D");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/missing");
        request.Headers.Add("X-Request-Id", requestId);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(requestId, Assert.Single(response.Headers.GetValues("X-Request-Id")));
        await AssertErrorAsync(response, "NOT_FOUND");
    }

    [Fact]
    public async Task HubAuthenticatesNegotiationAndWebSocketHandshake()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var host = factory.WithWebHostBuilder(builder => builder
            .UseSetting("Jwt:Issuer", "test-issuer")
            .UseSetting("Jwt:Audience", "test-audience")
            .UseSetting("Jwt:SigningKeyBase64", Convert.ToBase64String(key)));
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        using var anonymous = await client.PostAsync(NegotiatePath, null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await AssertErrorAsync(anonymous, "UNAUTHENTICATED");

        var token = CreateToken(key, [new Claim(JwtRegisteredClaimNames.Sub, "test-user")]);
        using var query = await client.PostAsync($"{NegotiatePath}&access_token={token}", null);
        Assert.Equal(HttpStatusCode.Unauthorized, query.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(key, []));
        using var forbidden = await client.PostAsync(NegotiatePath, null);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        await AssertErrorAsync(forbidden, "FORBIDDEN");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var authorized = await client.PostAsync(NegotiatePath, null);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await host.Server.CreateWebSocketClient().ConnectAsync(
            new Uri($"wss://localhost/hubs/messaging?access_token={token}"), timeout.Token);
        var handshake = "{\"protocol\":\"json\",\"version\":1}\u001e"u8.ToArray();
        await socket.SendAsync(handshake.AsMemory(), WebSocketMessageType.Text, true, timeout.Token);

        var buffer = new byte[256];
        var frame = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
        Assert.Equal(WebSocketMessageType.Text, frame.MessageType);
        Assert.Equal("{}\u001e", Encoding.UTF8.GetString(buffer, 0, frame.Count));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
    }

    [Fact]
    public async Task RateLimitUsesErrorEnvelopeAndExemptsProbes()
    {
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("RateLimiting:PermitLimit", "1"));
        using var client = host.CreateClient();

        using var first = await client.PostAsync(NegotiatePath, null);
        using var second = await client.PostAsync(NegotiatePath, null);
        using var live = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        await AssertErrorAsync(second, "RATE_LIMITED");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Theory]
    [InlineData("https://web.example", true)]
    [InlineData("https://other.example", false)]
    public async Task CorsReturnsHeaderOnlyForAllowedOrigin(string origin, bool allowed)
    {
        using var host = factory.WithWebHostBuilder(builder => builder
            .UseSetting("Cors:AllowedOrigins:0", "https://web.example"));
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, NegotiatePath);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");

        using var response = await client.SendAsync(request);

        Assert.Equal(allowed, response.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins));
        if (allowed)
        {
            Assert.Equal(origin, Assert.Single(origins!));
        }
    }

    public void Dispose() => factory.Dispose();

    private static string CreateToken(byte[] key, IEnumerable<Claim> claims)
    {
        var token = new JwtSecurityToken("test-issuer", "test-audience", claims,
            expires: DateTime.UtcNow.AddMinutes(1),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, string code)
    {
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.NotNull(envelope);
        Assert.Equal(code, envelope.Error.Code);
        Assert.Equal(Assert.Single(response.Headers.GetValues("X-Request-Id")), envelope.Error.RequestId);
        Assert.Null(envelope.Error.Details);
    }
}
