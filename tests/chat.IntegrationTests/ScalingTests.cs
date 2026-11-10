using chat.Api.Hubs;
using chat.Api.Messaging;
using chat.Api.Middleware;
using chat.Domain.Businesses;
using chat.Domain.Identity;
using chat.Domain.Messaging;
using chat.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace chat.IntegrationTests;

public sealed class ScalingTests(BackendFixture backend) : IClassFixture<BackendFixture>
{
    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task ConcurrentCreatesShareBusinessLockAndReturnOneConversationPerClient()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var scope = backend.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var (business, clients) = await SeedAsync(db);
        await using var sharedLock = await db.Database.BeginTransactionAsync(timeout.Token);
        await db.Businesses.FromSql($"SELECT * FROM \"Businesses\" WHERE \"Id\" = {business.Id} FOR SHARE")
            .AsNoTracking().ToListAsync(timeout.Token);

        // A message writer holds this same business lock. Creates must finish before it is released.
        var views = await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            await using var request = backend.Host.Services.CreateAsyncScope();
            var context = request.ServiceProvider.GetRequiredService<ChatDbContext>();
            await context.Database.OpenConnectionAsync(timeout.Token);
            await context.Database.ExecuteSqlRawAsync("SET lock_timeout = '2s'", timeout.Token);
            return await request.ServiceProvider.GetRequiredService<ConversationService>()
                .CreateAsync(clients[index % clients.Length].Id, business.Id, timeout.Token);
        }));

        for (var index = 0; index < clients.Length; index++)
        {
            Assert.Single(views.Where((_, position) => position % clients.Length == index).Select(view => view.Id).Distinct());
        }
        Assert.Equal(clients.Length, views.Select(view => view.Id).Distinct().Count());
        Assert.Equal(clients.Length, await db.Conversations.CountAsync(row => row.BusinessId == business.Id, timeout.Token));
    }

    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task CreateStillSerializesWithSuspensionAndRejectsSuspendedBusiness()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var scope = backend.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var (business, clients) = await SeedAsync(db);
        await using (var suspension = await db.Database.BeginTransactionAsync(timeout.Token))
        {
            await db.Businesses.Where(row => row.Id == business.Id).ExecuteUpdateAsync(update => update
                .SetProperty(row => row.IsActive, false).SetProperty(row => row.Status, BusinessStatus.SUSPENDED), timeout.Token);

            await using var request = backend.Host.Services.CreateAsyncScope();
            var context = request.ServiceProvider.GetRequiredService<ChatDbContext>();
            await context.Database.OpenConnectionAsync(timeout.Token);
            await context.Database.ExecuteSqlRawAsync("SET lock_timeout = '200ms'", timeout.Token);
            var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() => request.ServiceProvider
                .GetRequiredService<ConversationService>().CreateAsync(clients[0].Id, business.Id, timeout.Token));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, Assert.IsType<PostgresException>(conflict.InnerException).SqlState);
            await suspension.CommitAsync(timeout.Token);
        }

        await using var deniedRequest = backend.Host.Services.CreateAsyncScope();
        var denied = await Assert.ThrowsAsync<ApiFailure>(() => deniedRequest.ServiceProvider
            .GetRequiredService<ConversationService>().CreateAsync(clients[0].Id, business.Id, timeout.Token));
        Assert.Equal(404, denied.StatusCode);
        Assert.False(await db.Conversations.AnyAsync(row => row.BusinessId == business.Id, timeout.Token));
    }

    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task OutboxDrainsMoreThanOneFormerBatchBeforeFirstIdleDelay()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var scope = backend.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var future = DateTimeOffset.UtcNow.AddDays(2);
        var rows = Enumerable.Range(0, 60).Select(_ => new OutboxMessage
        {
            EventName = "DrainProbe",
            Payload = "{}",
            OccurredAt = future,
            AvailableAt = future
        }).ToArray();
        db.OutboxMessages.AddRange(rows);
        await db.SaveChangesAsync(timeout.Token);

        // Future availability isolates these rows from the fixture's regular dispatcher.
        var clock = new IdleClock(future.AddMinutes(1));
        using var worker = new OutboxWorker(backend.Host.Services.GetRequiredService<IServiceScopeFactory>(),
            new RealtimeConnections(), backend.Host.Services.GetRequiredService<IHubContext<MessagingHub>>(),
            Options.Create(new PushOptions()), clock, NullLogger<OutboxWorker>.Instance);
        await worker.StartAsync(timeout.Token);
        try
        {
            await clock.Idle.Task.WaitAsync(timeout.Token);
            var ids = rows.Select(row => row.Id).ToArray();
            Assert.Equal(rows.Length, await db.OutboxMessages.CountAsync(row => ids.Contains(row.Id)
                && row.CompletedAt != null && row.Outcome == "suppressed", timeout.Token));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task RetentionDrainsBacklogInBoundedRoundsAndPreservesPendingWork()
    {
        await using var scope = backend.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var ids = await SeedRetentionAsync(db, 405);
        var settings = new RetentionOptions { Approved = true, MaxBatchesPerSweep = 1, SweepBudgetSeconds = 60 };
        var monitor = new RetentionMonitor(() => settings);
        var worker = new RetentionWorker(backend.Host.Services.GetRequiredService<IServiceScopeFactory>(), monitor,
            TimeProvider.System, NullLogger<RetentionWorker>.Instance);
        await worker.DrainAsync(default);
        Assert.Equal(205, await db.OutboxMessages.CountAsync(row => ids.Contains(row.Id) && row.CompletedAt != null));
        settings.MaxBatchesPerSweep = 50;
        await worker.DrainAsync(default);
        Assert.Equal(1, await db.OutboxMessages.CountAsync(row => ids.Contains(row.Id)));
        Assert.True(await db.OutboxMessages.AnyAsync(row => ids.Contains(row.Id) && row.CompletedAt == null));
    }

    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task RetentionRechecksLegalHoldBeforeNextRoundAndCanResume()
    {
        await using var scope = backend.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var ids = await SeedRetentionAsync(db, 405);
        var active = new RetentionOptions { Approved = true, SweepBudgetSeconds = 60 };
        var held = new RetentionOptions { Approved = true, LegalHold = true };
        var reads = 0;
        // Initial settings and the first round are allowed; configuration changes before round two.
        var monitor = new RetentionMonitor(() => ++reads <= 2 ? active : held);
        var worker = new RetentionWorker(backend.Host.Services.GetRequiredService<IServiceScopeFactory>(), monitor,
            TimeProvider.System, NullLogger<RetentionWorker>.Instance);
        await worker.DrainAsync(default);
        Assert.Equal(205, await db.OutboxMessages.CountAsync(row => ids.Contains(row.Id) && row.CompletedAt != null));
        var resumed = new RetentionWorker(backend.Host.Services.GetRequiredService<IServiceScopeFactory>(),
            new RetentionMonitor(() => active), TimeProvider.System, NullLogger<RetentionWorker>.Instance);
        await resumed.DrainAsync(default);
        Assert.Equal(1, await db.OutboxMessages.CountAsync(row => ids.Contains(row.Id)));
    }

    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task RetentionStopsStartingRoundsWhenTimeBudgetExpires()
    {
        await using var scope = backend.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var ids = await SeedRetentionAsync(db, 405);
        var clock = new RetentionClock();
        var reads = 0;
        var monitor = new RetentionMonitor(() =>
        {
            if (++reads == 2) clock.Elapsed = TimeSpan.FromSeconds(6);
            return new RetentionOptions { Approved = true, SweepBudgetSeconds = 5 };
        });
        var worker = new RetentionWorker(backend.Host.Services.GetRequiredService<IServiceScopeFactory>(), monitor,
            clock, NullLogger<RetentionWorker>.Instance);
        await worker.DrainAsync(default);
        Assert.Equal(205, await db.OutboxMessages.CountAsync(row => ids.Contains(row.Id) && row.CompletedAt != null));
    }

    private sealed class RetentionClock : TimeProvider
    {
        public TimeSpan Elapsed { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
    }

    private static async Task<Guid[]> SeedRetentionAsync(ChatDbContext db, int count)
    {
        // Class fixtures share a database; remove only this suite's synthetic rows between scenarios.
        await db.OutboxMessages.Where(row => row.EventName == "RetentionScaleProbe").ExecuteDeleteAsync();
        var old = DateTimeOffset.UtcNow.AddDays(-10);
        var rows = Enumerable.Range(0, count + 1).Select(index => new OutboxMessage
        {
            EventName = "RetentionScaleProbe",
            Payload = "{}",
            OccurredAt = old,
            AvailableAt = DateTimeOffset.UtcNow.AddDays(1),
            CompletedAt = index < count ? old : null
        }).ToArray();
        db.OutboxMessages.AddRange(rows);
        await db.SaveChangesAsync();
        return rows.Select(row => row.Id).ToArray();
    }

    private sealed class RetentionMonitor(Func<RetentionOptions> current) : IOptionsMonitor<RetentionOptions>
    {
        public RetentionOptions CurrentValue => current();
        public RetentionOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<RetentionOptions, string?> listener) => null;
    }

    private static async Task<(Business Business, User[] Clients)> SeedAsync(ChatDbContext db)
    {
        var clients = Enumerable.Range(0, 2).Select(_ =>
        {
            var email = $"{Guid.NewGuid():N}@example.test";
            return new User
            {
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                DisplayName = "Scaling test",
                Kind = UserKind.CLIENT,
                EmailVerifiedAt = DateTimeOffset.UtcNow
            };
        }).ToArray();
        var business = new Business
        {
            LegalName = "Scaling test",
            DisplayName = "Scaling test",
            RegistrationNumber = Guid.NewGuid().ToString("N"),
            Country = "KZ",
            Category = "Test",
            Status = BusinessStatus.VERIFIED,
            IsActive = true
        };
        db.Users.AddRange(clients);
        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        return (business, clients);
    }

    private sealed class IdleClock(DateTimeOffset now) : TimeProvider
    {
        public TaskCompletionSource Idle { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Idle.TrySetResult();
            return new PausedTimer();
        }

        private sealed class PausedTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
