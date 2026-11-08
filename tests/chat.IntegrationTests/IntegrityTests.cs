using System.Buffers.Binary;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using chat.Api.DependencyInjection;
using chat.Api.Messaging;
using chat.Contracts;
using chat.Contracts.Messaging;
using chat.Domain.Businesses;
using chat.Domain.Identity;
using chat.Domain.Messaging;
using chat.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Xunit;

namespace chat.IntegrationTests;

public sealed class IntegrityTests(BackendFixture backend) : IClassFixture<BackendFixture>
{
    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task DatabaseRejectsInvalidConversationKindAndCrossConversationMessageReferences()
    {
        await using var scope = backend.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var client = await SeedActorAsync(db);
        var staff = await SeedActorAsync(db, UserKind.BUSINESS_MEMBER);
        var business = NewBusiness();
        var other = NewBusiness();
        var room = NewRoom(client.Id, business.Id);
        var foreign = NewRoom(client.Id, other.Id);
        var message = NewMessage(room, client.Id);
        db.AddRange(business, other, room, foreign, message);
        await db.SaveChangesAsync();

        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var wrongKind = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Conversations" ("Id", "BusinessId", "ClientUserId", "CreatedAt", "UpdatedAt")
            VALUES ({id}, {business.Id}, {staff.Id}, {now}, {now})
            """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, wrongKind.SqlState);

        var wrongReceipt = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "MessageReadReceipts" ("ConversationId", "Side", "LastMessageId", "LastMessageAt", "UpdatedAt")
            VALUES ({foreign.Id}, 'CLIENT', {message.Id}, {now}, {now})
            """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, wrongReceipt.SqlState);

        db.Attachments.Add(new Attachment
        {
            ConversationId = foreign.Id,
            UploaderUserId = client.Id,
            ContentType = "application/pdf",
            Size = 12,
            UploadExpiresAt = now.AddMinutes(5),
            MessageId = message.Id,
            Status = AttachmentStatus.READY
        });
        var wrongAttachment = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(wrongAttachment.InnerException).SqlState);
    }

    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task InboxVersionedCursorKeepsOrderAcrossMessagesAndAppliesLiveAcl()
    {
        await using var scope = backend.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var actor = await SeedActorAsync(db);
        var outsider = await SeedActorAsync(db);
        using var client = Client(actor);
        var sameTime = DateTimeOffset.UtcNow.AddDays(-1);
        var businesses = Enumerable.Range(0, 4).Select(_ => NewBusiness()).ToArray();
        var rooms = businesses.Take(3).Select(company => NewRoom(actor.Id, company.Id, sameTime))
            .OrderByDescending(row => row.Id.ToString("D"), StringComparer.Ordinal).ToArray();
        db.AddRange(businesses);
        db.AddRange(rooms);
        db.Add(NewRoom(outsider.Id, businesses[3].Id, sameTime.AddHours(1)));
        await db.SaveChangesAsync();

        var first = (await client.GetFromJsonAsync<PageResponse<ConversationView>>("/v1/conversations?limit=1"))!;
        Assert.Equal(rooms[0].Id, Assert.Single(first.Items).Id);
        Assert.StartsWith("v2.", first.NextCursor);
        var move = new SendMessageRequest(Guid.NewGuid(), "TEXT", "Message during paging", null);
        using var sent = await client.PostAsJsonAsync($"/v1/conversations/{rooms[2].Id}/messages", move);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        await db.Businesses.Where(row => row.Id == rooms[1].BusinessId).ExecuteUpdateAsync(update => update
            .SetProperty(row => row.IsActive, false).SetProperty(row => row.Status, BusinessStatus.SUSPENDED));
        var next = (await client.GetFromJsonAsync<PageResponse<ConversationView>>($"/v1/conversations?limit=1&cursor={first.NextCursor}"))!;
        Assert.Equal(rooms[2].Id, Assert.Single(next.Items).Id);
        Assert.Null(next.NextCursor);
        using var invalid = await client.GetAsync("/v1/conversations?cursor=v3.invalid");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var bytes = new byte[24];
        BinaryPrimitives.WriteInt64BigEndian(bytes, DateTimeOffset.UtcNow.AddDays(1).UtcTicks);
        rooms[0].Id.TryWriteBytes(bytes.AsSpan(8));
        var legacy = (await client.GetFromJsonAsync<PageResponse<ConversationView>>(
            $"/v1/conversations?limit=1&cursor={Base64UrlEncoder.Encode(bytes)}"))!;
        Assert.Equal(rooms[2].Id, Assert.Single(legacy.Items).Id);
        Assert.Equal(32, legacy.NextCursor!.Length);
    }

    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task CorrectlySignedTokensWithWrongIssuerAudienceOrExpiryAreRejected()
    {
        await using var scope = backend.Host.Services.CreateAsyncScope();
        var actor = await SeedActorAsync(scope.ServiceProvider.GetRequiredService<ChatDbContext>());
        var jwt = backend.Host.Services.GetRequiredService<IOptions<JwtSettings>>().Value;
        using var client = Client(actor);
        using var valid = await client.GetAsync("/v1/me");
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        foreach (var invalid in new[]
                 {
                     Token(actor, "wrong-issuer", jwt.Audience, DateTimeOffset.UtcNow.AddMinutes(5)),
                     Token(actor, jwt.Issuer, "wrong-audience", DateTimeOffset.UtcNow.AddMinutes(5)),
                     Token(actor, jwt.Issuer, jwt.Audience, DateTimeOffset.UtcNow.AddSeconds(-5))
                 })
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", invalid);
            using var response = await client.GetAsync("/v1/me");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("UNAUTHENTICATED", (await response.Content.ReadFromJsonAsync<ErrorEnvelope>())?.Error.Code);
        }
    }

    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task ExpoOwnershipRequiresReleaseAndOldFamilyCannotDeliverAfterHandoff()
    {
        await using var scope = backend.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var owner = await SeedActorAsync(db);
        var rival = await SeedActorAsync(db);
        var another = await SeedActorAsync(db);
        var business = NewBusiness();
        var room = NewRoom(owner.Id, business.Id);
        db.AddRange(business, room);
        await db.SaveChangesAsync();

        using var original = Client(owner);
        using var competitor = Client(rival);
        using var secondCompetitor = Client(another);
        var token = new RegisterDeviceRequest($"ExpoPushToken[{Guid.NewGuid():N}]");
        using var enrolled = await original.PostAsJsonAsync("/v1/devices", token);
        Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode);
        var device = (await enrolled.Content.ReadFromJsonAsync<RegisteredDevice>())!;
        using var hijack = await competitor.PostAsJsonAsync("/v1/devices", token);
        Assert.Equal(HttpStatusCode.Conflict, hijack.StatusCode);

        using var logout = await original.PostAsync("/v1/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var stale = await original.PostAsJsonAsync("/v1/devices", token);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        using var afterLogout = await competitor.PostAsJsonAsync("/v1/devices", token);
        Assert.Equal(HttpStatusCode.Conflict, afterLogout.StatusCode);
        var renewed = await SeedFamilyAsync(db, owner.Id);
        using var current = Client(renewed);
        using var release = await current.DeleteAsync($"/v1/devices/{device.Id}");
        Assert.Equal(HttpStatusCode.NoContent, release.StatusCode);
        var racers = await Task.WhenAll(competitor.PostAsJsonAsync("/v1/devices", token), secondCompetitor.PostAsJsonAsync("/v1/devices", token));
        using var transferred = Assert.Single(racers, item => item.StatusCode == HttpStatusCode.OK);
        using var refused = Assert.Single(racers, item => item.StatusCode == HttpStatusCode.Conflict);

        var now = DateTimeOffset.UtcNow;
        var queued = new PushDelivery
        {
            EventId = Guid.NewGuid(),
            DeviceId = device.Id,
            UserId = owner.Id,
            FamilyId = owner.Family,
            ConversationId = room.Id,
            AvailableAt = now,
            ExpiresAt = now.AddMinutes(2)
        };
        db.PushDeliveries.Add(queued);
        await db.SaveChangesAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!await db.PushDeliveries.AnyAsync(row => row.Id == queued.Id && row.Outcome == "suppressed", timeout.Token))
        {
            await Task.Delay(100, timeout.Token);
        }
        Assert.False(backend.Push.Sends.Reader.TryRead(out _));
    }

    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task RetentionRespectsHoldAndResumesBoundedCleanupAfterFailure()
    {
        using var enabled = backend.Host.WithWebHostBuilder(builder => builder.UseSetting("Retention:Approved", "true"));
        using var held = backend.Host.WithWebHostBuilder(builder => builder.UseSetting("Retention:Approved", "true")
            .UseSetting("Retention:LegalHold", "true"));
        await using var scope = backend.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var actor = await SeedActorAsync(db);
        var now = DateTimeOffset.UtcNow;
        var old = now.AddDays(-10);
        var replay = new RefreshSession
        {
            UserId = actor.Id,
            FamilyId = actor.Family,
            TokenHash = Guid.NewGuid().ToString("N"),
            ConsumedAt = now.AddDays(-9),
            ExpiresAt = now.AddDays(1)
        };
        var expired = new RefreshSession
        {
            UserId = actor.Id,
            FamilyId = Guid.NewGuid(),
            TokenHash = Guid.NewGuid().ToString("N"),
            ExpiresAt = old
        };
        db.AddRange(replay, expired);
        db.OutboxMessages.AddRange(Enumerable.Range(0, 205).Select(_ => new OutboxMessage
        {
            EventName = "RetentionProbe",
            Payload = "{}",
            AvailableAt = old,
            OccurredAt = old,
            CompletedAt = old
        }));
        var pending = new OutboxMessage
        {
            EventName = "RetentionProbe",
            Payload = "{}",
            AvailableAt = now.AddDays(1),
            OccurredAt = old
        };
        db.OutboxMessages.Add(pending);
        var business = NewBusiness();
        var room = NewRoom(actor.Id, business.Id);
        var staleUpload = new Attachment
        {
            ConversationId = room.Id,
            UploaderUserId = actor.Id,
            ContentType = "application/pdf",
            Size = 12,
            UploadExpiresAt = old
        };
        db.AddRange(business, room, staleUpload);
        await db.SaveChangesAsync();

        var heldWorker = held.Services.GetServices<IHostedService>().OfType<RetentionWorker>().Single();
        await heldWorker.SweepAsync(default);
        Assert.Equal(206, await db.OutboxMessages.CountAsync(row => row.EventName == "RetentionProbe"));
        Assert.False(await held.Services.GetServices<IHostedService>().OfType<AttachmentCleanupWorker>().Single().CleanNextAsync(default));
        Assert.False(await db.Attachments.AnyAsync(row => row.Id == staleUpload.Id && row.RetiredAt != null));

        var worker = enabled.Services.GetServices<IHostedService>().OfType<RetentionWorker>().Single();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION retention_test_error() RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN RAISE EXCEPTION 'Simulated retention failure'; END
            $body$;
            CREATE TRIGGER retention_test_error BEFORE DELETE ON "OutboxMessages"
            FOR EACH STATEMENT EXECUTE FUNCTION retention_test_error();
            """);
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => worker.SweepAsync(default));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("""DROP TRIGGER retention_test_error ON "OutboxMessages"; DROP FUNCTION retention_test_error();""");
        }

        await worker.SweepAsync(default);
        Assert.Equal(6, await db.OutboxMessages.CountAsync(row => row.EventName == "RetentionProbe"));
        await worker.SweepAsync(default);
        await worker.SweepAsync(default);
        Assert.True(await db.OutboxMessages.AnyAsync(row => row.Id == pending.Id));
        Assert.False(await db.RefreshSessions.AnyAsync(row => row.Id == expired.Id));
        Assert.True(await db.RefreshSessions.AnyAsync(row => row.Id == replay.Id));
    }

    private static async Task<Actor> SeedActorAsync(ChatDbContext db, UserKind kind = UserKind.CLIENT)
    {
        var email = $"{Guid.NewGuid():N}@example.test";
        var user = new User
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = "Test",
            Kind = kind,
            EmailVerifiedAt = DateTimeOffset.UtcNow
        };
        db.Users.Add(user);
        return await SeedFamilyAsync(db, user.Id);
    }

    private static async Task<Actor> SeedFamilyAsync(ChatDbContext db, Guid user)
    {
        var family = Guid.NewGuid();
        db.RefreshSessions.Add(new RefreshSession
        {
            UserId = user,
            FamilyId = family,
            TokenHash = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        });
        await db.SaveChangesAsync();
        return new Actor(user, family);
    }

    private HttpClient Client(Actor actor)
    {
        var jwt = backend.Host.Services.GetRequiredService<IOptions<JwtSettings>>().Value;
        var client = backend.Host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            Token(actor, jwt.Issuer, jwt.Audience, DateTimeOffset.UtcNow.AddMinutes(5)));
        return client;
    }

    private string Token(Actor actor, string? issuer, string? audience, DateTimeOffset expiry)
    {
        var key = backend.Host.Services.GetRequiredService<IOptions<JwtSettings>>().Value.GetSigningKey();
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer, audience,
            [new Claim(JwtRegisteredClaimNames.Sub, actor.Id.ToString()), new Claim(JwtRegisteredClaimNames.Sid, actor.Family.ToString())],
            DateTimeOffset.UtcNow.AddMinutes(-1).UtcDateTime, expiry.UtcDateTime, new SigningCredentials(key!, SecurityAlgorithms.HmacSha256)));
    }

    private static Business NewBusiness() => new()
    {
        LegalName = "Test",
        DisplayName = "Test",
        RegistrationNumber = Guid.NewGuid().ToString("N"),
        Country = "KZ",
        Category = "Test",
        Status = BusinessStatus.VERIFIED,
        IsActive = true
    };

    private static Conversation NewRoom(Guid client, Guid business, DateTimeOffset? created = null) => new()
    {
        ClientUserId = client,
        BusinessId = business,
        CreatedAt = created ?? DateTimeOffset.UtcNow,
        UpdatedAt = created ?? DateTimeOffset.UtcNow
    };

    private static Message NewMessage(Conversation room, Guid author) => new()
    {
        ConversationId = room.Id,
        AuthorUserId = author,
        AuthorSide = ParticipantSide.CLIENT,
        ClientMessageId = Guid.NewGuid(),
        Type = MessageType.TEXT,
        Text = "test",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private sealed record Actor(Guid Id, Guid Family);
}
