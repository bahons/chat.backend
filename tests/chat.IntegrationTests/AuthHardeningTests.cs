using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using chat.Contracts;
using chat.Contracts.Identity;
using chat.Domain.Identity;
using chat.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace chat.IntegrationTests;

public sealed class AuthHardeningTests(BackendFixture backend) : IClassFixture<BackendFixture>
{
    private const string Password = "integration-test-password";

    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task LoginLimitsNormalizedAccountAndTrustedAddressAndRecoversAfterWindow()
    {
        var clock = new LoginClock();
        using var host = backend.Host.WithWebHostBuilder(builder => builder.UseEnvironment("Production")
            .UseSetting("ReverseProxy:KnownProxies:0", "192.0.2.10")
            .UseSetting("Identity:LoginAttemptsPerAccount", "2")
            .UseSetting("Identity:LoginAttemptsPerAddress", "2")
            .UseSetting("Identity:LoginKeyCapacity", "4")
            .ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock))));
        var email = await SeedAsync(host);

        await AssertLoginAsync(host, "192.0.2.11", "203.0.113.1", email, 403);
        await AssertLoginAsync(host, "192.0.2.10", "203.0.113.1", email, 401);
        await AssertLoginAsync(host, "192.0.2.10", "203.0.113.2", email.ToUpperInvariant(), 401);
        await AssertLoginAsync(host, "192.0.2.10", "203.0.113.3", email, 429, Password);
        await AssertLoginAsync(host, "192.0.2.10", "203.0.113.3", "missing-a@example.test", 401);
        await AssertLoginAsync(host, "192.0.2.10", "203.0.113.3", "missing-b@example.test", 401);
        await AssertLoginAsync(host, "192.0.2.10", "203.0.113.3", "missing-c@example.test", 429);
        await AssertLoginAsync(host, "192.0.2.10", "203.0.113.4", "missing-c@example.test", 401);
        await AssertLoginAsync(host, "192.0.2.10", "203.0.113.5", "missing-d@example.test", 429);
        clock.Advance();
        await AssertLoginAsync(host, "192.0.2.10", "203.0.113.3", email, 200, Password);
    }

    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task ConcurrentRefreshRevokesWinnerAndLostResponseCannotBeReplayed()
    {
        using var client = backend.Host.CreateClient();
        var email = await SeedAsync(backend.Host);
        var original = await LoginAsync(client, email);
        var requests = await Task.WhenAll(RefreshAsync(client, original.RefreshToken), RefreshAsync(client, original.RefreshToken));
        using var success = Assert.Single(requests, response => response.StatusCode == HttpStatusCode.OK);
        using var replay = Assert.Single(requests, response => response.StatusCode == HttpStatusCode.Unauthorized);
        var winner = (await success.Content.ReadFromJsonAsync<AuthTokens>())!;
        await AssertUnauthorizedAsync(replay);
        using var winnerRefresh = await RefreshAsync(client, winner.RefreshToken);
        await AssertUnauthorizedAsync(winnerRefresh);
        await AssertAccessRevokedAsync(client, winner.AccessToken);

        var lost = await LoginAsync(client, email);
        using (var discarded = await RefreshAsync(client, lost.RefreshToken))
        {
            Assert.Equal(HttpStatusCode.OK, discarded.StatusCode);
        }

        using var retry = await RefreshAsync(client, lost.RefreshToken);
        await AssertUnauthorizedAsync(retry);
        await AssertAccessRevokedAsync(client, lost.AccessToken);
    }

    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task RefreshLockTimeoutIsNeutralAndExpiredFamilyCannotRotate()
    {
        using var client = backend.Host.CreateClient();
        var email = await SeedAsync(backend.Host);
        var original = await LoginAsync(client, email);
        var schema = JsonNode.Parse(await client.GetStringAsync("/openapi/v1.json"));
        Assert.NotNull(schema?["paths"]?["/v1/auth/refresh"]?["post"]?["responses"]?["503"]?["content"]?["application/json"]);
        await using var scope = backend.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var userId = await db.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Users.FromSql($"SELECT * FROM \"Users\" WHERE \"Id\" = {userId} FOR UPDATE").ToListAsync();
            using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var blocked = await client.PostAsJsonAsync("/v1/auth/refresh", new RefreshRequest(original.RefreshToken),
                bounded.Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
            var error = (await blocked.Content.ReadFromJsonAsync<ErrorEnvelope>())!.Error;
            Assert.Equal("INTERNAL_ERROR", error.Code);
            Assert.Null(error.Details);
            await transaction.CommitAsync();
        }

        using var rotated = await RefreshAsync(client, original.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var tokens = (await rotated.Content.ReadFromJsonAsync<AuthTokens>())!;
        await db.RefreshSessions.Where(session => session.UserId == userId)
            .ExecuteUpdateAsync(set => set.SetProperty(session => session.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        using var expired = await RefreshAsync(client, tokens.RefreshToken);
        await AssertUnauthorizedAsync(expired);
        await AssertAccessRevokedAsync(client, tokens.AccessToken);
    }

    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task LoginHashDoesNotBlockRefreshOrResetAndCannotIssueAfterPasswordChanged()
    {
        using var gate = new PausedHasher();
        using var host = backend.Host.WithWebHostBuilder(builder => builder
            .ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<IPasswordHasher<User>>(gate))));
        using var client = host.CreateClient();
        var email = await SeedAsync(host);
        var original = await LoginAsync(client, email);
        using var forgot = await client.PostAsJsonAsync("/v1/auth/password/forgot", new ForgotPasswordRequest(email));
        Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);
        var code = await backend.ReadCodeAsync("chat password reset");
        gate.Arm();
        var staleLogin = client.PostAsJsonAsync("/v1/auth/login", new LoginRequest(email, Password));
        AuthTokens rotated;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var refresh = await client.PostAsJsonAsync("/v1/auth/refresh", new RefreshRequest(original.RefreshToken),
                timeout.Token);
            Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
            rotated = (await refresh.Content.ReadFromJsonAsync<AuthTokens>(timeout.Token))!;
            using var reset = await client.PostAsJsonAsync("/v1/auth/password/reset",
                new ResetPasswordRequest(code, "new-integration-test-password"), timeout.Token);
            Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        }
        finally
        {
            gate.Release();
        }

        using var stale = await staleLogin;
        await AssertUnauthorizedAsync(stale);
        await AssertAccessRevokedAsync(client, rotated.AccessToken);
        _ = await LoginAsync(client, email, "new-integration-test-password");
    }

    private static async Task AssertLoginAsync(WebApplicationFactory<Program> host, string peer, string forwardedIp,
        string email, int expected, string password = "invalid-password")
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new LoginRequest(email, password), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var response = await host.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            context.Request.Method = "POST";
            context.Request.Path = "/v1/auth/login";
            context.Request.Scheme = "https";
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = body.Length;
            context.Request.Body = new MemoryStream(body);
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestHasBody());
            context.Request.Headers["X-Forwarded-For"] = forwardedIp;
            context.Request.Headers["X-Forwarded-Proto"] = "https";
        });
        Assert.Equal(expected, response.Response.StatusCode);
        if (expected is 401 or 429)
        {
            var envelope = (await JsonSerializer.DeserializeAsync<ErrorEnvelope>(response.Response.Body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
            Assert.Null(envelope.Error.Details);
            if (expected == 401)
            {
                Assert.Equal("UNAUTHENTICATED", envelope.Error.Code);
                Assert.Equal("Authentication is required.", envelope.Error.Message);
            }
            else
            {
                Assert.Equal("RATE_LIMITED", envelope.Error.Code);
                Assert.InRange(int.Parse(response.Response.Headers.RetryAfter.ToString()), 1, 60);
            }
        }
    }

    private static async Task<string> SeedAsync(WebApplicationFactory<Program> host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var email = $"{Guid.NewGuid():N}@example.test";
        var user = new User { Email = email, NormalizedEmail = email.ToUpperInvariant(), DisplayName = "Test", EmailVerifiedAt = DateTimeOffset.UtcNow };
        user.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>().HashPassword(user, Password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return email;
    }

    private static async Task<AuthTokens> LoginAsync(HttpClient client, string email, string password = Password)
    {
        using var result = await client.PostAsJsonAsync("/v1/auth/login", new LoginRequest(email, password));
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        return (await result.Content.ReadFromJsonAsync<AuthTokens>())!;
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string token) =>
        client.PostAsJsonAsync("/v1/auth/refresh", new RefreshRequest(token));

    private static async Task AssertUnauthorizedAsync(HttpResponseMessage result)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        var error = (await result.Content.ReadFromJsonAsync<ErrorEnvelope>())!.Error;
        Assert.Equal("UNAUTHENTICATED", error.Code);
        Assert.Null(error.Details);
    }

    private static async Task AssertAccessRevokedAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var result = await client.SendAsync(request);
        await AssertUnauthorizedAsync(result);
    }

    private sealed class LoginClock : TimeProvider
    {
        private long seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Interlocked.Read(ref seconds);
        public void Advance() => Interlocked.Add(ref seconds, 61);
    }

    private sealed class RequestHasBody : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class PausedHasher : IPasswordHasher<User>, IDisposable
    {
        private readonly PasswordHasher<User> inner = new(Options.Create(new PasswordHasherOptions { IterationCount = 210_000 }));
        private readonly ManualResetEventSlim resume = new();
        private int armed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm() => Interlocked.Exchange(ref armed, 1);
        public void Release() => resume.Set();
        public string HashPassword(User user, string password) => inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(User user, string hashedPassword, string providedPassword)
        {
            if (Interlocked.Exchange(ref armed, 0) == 1)
            {
                Entered.TrySetResult();
                if (!resume.Wait(TimeSpan.FromSeconds(20)))
                {
                    throw new TimeoutException("Test verification was not released.");
                }
            }

            return inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }

        public void Dispose() => resume.Dispose();
    }
}
