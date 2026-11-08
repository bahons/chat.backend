using System.Net;
using System.Net.Http.Json;
using System.Text;
using chat.Api.Messaging;
using chat.Api.Identity;
using chat.Application.Identity;
using chat.Contracts.Identity;
using chat.Domain.Identity;
using chat.Domain.Messaging;
using chat.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using Xunit;

namespace chat.IntegrationTests;

public sealed class WorkerReliabilityTests(BackendFixture backend) : IClassFixture<BackendFixture>
{
    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task DatabaseGuardRejectsSecondApiAndAllowsRestartAfterStop()
    {
        using var first = backend.Host.WithWebHostBuilder(builder => builder.UseSetting("Runtime:EnforceSingleReplica", "true"));
        using var firstClient = first.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await firstClient.GetAsync("/health/live")).StatusCode);

        using var second = backend.Host.WithWebHostBuilder(builder => builder.UseSetting("Runtime:EnforceSingleReplica", "true"));
        var rejected = Assert.Throws<InvalidOperationException>(() => second.CreateClient());
        Assert.Contains("owns the database dispatch lock", rejected.Message);

        await first.DisposeAsync();
        using var replacement = backend.Host.WithWebHostBuilder(builder => builder.UseSetting("Runtime:EnforceSingleReplica", "true"));
        using var client = replacement.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);

        var lifetime = replacement.Services.GetRequiredService<IHostApplicationLifetime>();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStopping.Register(() => stopped.TrySetResult());
        await using var scope = backend.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var terminated = await db.Database.SqlQuery<bool>($"""
            SELECT pg_terminate_backend(pid) AS "Value" FROM pg_locks
            WHERE locktype = 'advisory' AND classid = 924601 AND objid = 1 AND objsubid = 2 AND granted
            """).SingleAsync();
        Assert.True(terminated);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task WorkerLagAlertsWithoutExposingPayload()
    {
        var now = DateTimeOffset.UtcNow;
        await using var scope = backend.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        db.OutboxMessages.Add(new OutboxMessage
        {
            EventName = "Test",
            Payload = """{"data":"sensitive-test-value"}""",
            OccurredAt = now.AddMinutes(-5),
            AvailableAt = now.AddMinutes(5)
        });
        await db.SaveChangesAsync();

        var logger = new BacklogLogger();
        var monitor = new WorkerBacklogMonitor(backend.Host.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new PushOptions()), TimeProvider.System, logger);
        await monitor.CheckBacklogAsync(default);
        Assert.Equal((233, "Worker backlog overdue: queue=outbox count_capped=1"), Assert.Single(logger.Messages));
    }

    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task QueuedMailSurvivesHostRestartBeforeFirstSend()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await postgres.StartAsync(timeout.Token);
        var mailKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var sender = new RestartMailSender();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Postgres", postgres.GetConnectionString())
            .UseSetting("AccountEmail:QueueKeyBase64", mailKey)
            .ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<IAccountEmailSender>(sender))));
        var email = $"{Guid.NewGuid():N}@example.test";
        using (var producer = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            var worker = services.Single(service => service.ServiceType == typeof(IHostedService)
                && service.ImplementationType == typeof(AccountEmailDelivery));
            services.Remove(worker);
        })))
        {
            await using var scope = producer.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ChatDbContext>().Database.MigrateAsync(timeout.Token);
            using var client = producer.CreateClient();
            using var response = await client.PostAsJsonAsync("/v1/auth/client/register",
                new RegisterClientRequest(email, "integration-test-password", "Test"), timeout.Token);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.False(sender.Mail.Task.IsCompleted);
        }

        using var consumer = factory.CreateClient();
        var message = await sender.Mail.Task.WaitAsync(TimeSpan.FromSeconds(10), timeout.Token);
        Assert.Equal(email, message.Recipient);
        var token = message.Body.Split(' ')[^1];
        using var verified = await consumer.PostAsJsonAsync("/v1/auth/email/verify", new VerifyEmailRequest(token), timeout.Token);
        Assert.Equal(HttpStatusCode.NoContent, verified.StatusCode);
    }

    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task ExpiredClaimSurvivesAbandonedWorkerAndFencesLateAcknowledgement()
    {
        var future = DateTimeOffset.UtcNow.AddDays(2);
        var id = Guid.NewGuid();
        await using var scope = backend.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var user = new User { Email = $"{id:N}@example.test", NormalizedEmail = $"{id:N}@EXAMPLE.TEST", DisplayName = "Test", PasswordHash = "-" };
        db.Add(user);
        db.Add(new AccountMail
        {
            Id = id,
            UserId = user.Id,
            IsPasswordReset = false,
            CreatedAt = future,
            AvailableAt = future,
            ExpiresAt = future.AddHours(1)
        });
        await db.SaveChangesAsync();

        await using var scopeA = backend.Host.Services.CreateAsyncScope();
        await using var scopeB = backend.Host.Services.CreateAsyncScope();
        var contextA = scopeA.ServiceProvider.GetRequiredService<ChatDbContext>();
        var contextB = scopeB.ServiceProvider.GetRequiredService<ChatDbContext>();
        var racers = await Task.WhenAll(ClaimAsync(contextA, id, future), ClaimAsync(contextB, id, future));
        var old = Assert.Single(racers.OfType<AccountMail>());
        var oldContext = ReferenceEquals(old, racers[0]) ? contextA : contextB;

        await using var restarted = backend.Host.Services.CreateAsyncScope();
        var replacement = restarted.ServiceProvider.GetRequiredService<ChatDbContext>();
        Assert.Null(await ClaimAsync(replacement, id, future.AddSeconds(59)));
        var recovered = (await ClaimAsync(replacement, id, future.AddSeconds(61)))!;
        Assert.Equal(2, recovered.Attempts);
        Assert.NotEqual(old.LeaseId, recovered.LeaseId);
        WorkerLease.Finish(old, future.AddSeconds(62), "submitted");
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => oldContext.SaveChangesAsync());
        WorkerLease.Finish(recovered, future.AddSeconds(63), "submitted");
        await replacement.SaveChangesAsync();
        Assert.Null(await ClaimAsync(replacement, id, future.AddMinutes(3)));
    }

    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task MailIntentIsEncryptedAndRetriesWithSameIdentityAfterOutageAndQueuePressure()
    {
        using var pressureHost = backend.Host.WithWebHostBuilder(builder => builder.UseSetting("AccountEmail:QueueCapacity", "1"));
        using var client = pressureHost.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var email = $"{Guid.NewGuid():N}@example.test";
        var first = new TaskCompletionSource<AccountEmail>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retry = new TaskCompletionSource<AccountEmail>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = 0;
        backend.BeforeMailSend = async (mail, ct) =>
        {
            if (mail.Recipient == email && Interlocked.Increment(ref call) == 1)
            {
                first.TrySetResult(mail);
                await release.Task.WaitAsync(ct);
                throw new IOException("Simulated SMTP acknowledgement loss");
            }

            if (mail.Recipient == email) retry.TrySetResult(mail);
        };
        try
        {
            using var registered = await client.PostAsJsonAsync("/v1/auth/client/register",
                new RegisterClientRequest(email, "integration-test-password", "Test"), timeout.Token);
            Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
            Assert.Equal("", await registered.Content.ReadAsStringAsync(timeout.Token));
            var original = await first.Task.WaitAsync(timeout.Token);
            var intentId = Assert.IsType<Guid>(original.IntentId);
            var token = original.Body.Split(' ')[^1];
            await using var scope = backend.Host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            var intent = await db.AccountMails.AsNoTracking().SingleAsync(row => row.Id == intentId, timeout.Token);
            Assert.Null(intent.CompletedAt);
            Assert.Equal(71, intent.Payload!.Length);
            Assert.DoesNotContain(token, Encoding.UTF8.GetString(intent.Payload));

            var otherEmail = $"{Guid.NewGuid():N}@example.test";
            using var overflow = await client.PostAsJsonAsync("/v1/auth/client/register",
                new RegisterClientRequest(otherEmail, "integration-test-password", "Test"), timeout.Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, overflow.StatusCode);
            Assert.False(await db.Users.AnyAsync(row => row.Email == otherEmail, timeout.Token));

            foreach (var address in new[] { email, otherEmail })
            {
                using var forgot = await client.PostAsJsonAsync("/v1/auth/password/forgot", new ForgotPasswordRequest(address), timeout.Token);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, forgot.StatusCode);
            }

            release.TrySetResult();
            Assert.Equal(original, await retry.Task.WaitAsync(timeout.Token));
            while (await db.AccountMails.AnyAsync(row => row.Id == intentId && row.Payload != null, timeout.Token))
            {
                await Task.Delay(50, timeout.Token);
            }
            Assert.Equal("submitted", await db.AccountMails.Where(row => row.Id == intentId).Select(row => row.Outcome).SingleAsync(timeout.Token));
        }
        finally
        {
            release.TrySetResult();
            backend.BeforeMailSend = null;
        }
    }

    private static Task<AccountMail?> ClaimAsync(ChatDbContext db, Guid id, DateTimeOffset now) =>
        WorkerLease.ClaimAsync<AccountMail>(db, $"""
            SELECT * FROM "AccountMails" WHERE "Id" = {id} AND "CompletedAt" IS NULL AND "AvailableAt" <= {now}
            FOR UPDATE SKIP LOCKED
            """, now, default);

    private sealed class RestartMailSender : IAccountEmailSender
    {
        public TaskCompletionSource<AccountEmail> Mail { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task SendAsync(AccountEmail message, CancellationToken cancellationToken)
        {
            Mail.TrySetResult(message);
            return Task.CompletedTask;
        }
    }

    private sealed class BacklogLogger : ILogger<WorkerBacklogMonitor>
    {
        public List<(int, string)> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add((eventId.Id, formatter(state, exception)));
    }
}
