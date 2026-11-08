using System.Net;
using System.Security.Cryptography;
using chat.Domain.Businesses;
using chat.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace chat.IntegrationTests;

public sealed class PostgresTests
{
    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task ReadinessRequiresMigrationsAndSigningConfiguration()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await postgres.StartAsync(timeout.Token);

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Postgres", postgres.GetConnectionString()));
        using var configured = factory.WithWebHostBuilder(builder => builder
            .UseSetting("Jwt:Issuer", "readiness-tests")
            .UseSetting("Jwt:Audience", "readiness-tests")
            .UseSetting("Jwt:SigningKeyBase64", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        using var client = configured.CreateClient();
        using var empty = await client.GetAsync("/health/ready", timeout.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, empty.StatusCode);
        using var live = await client.GetAsync("/health/live", timeout.Token);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        await using (var scope = configured.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            Assert.False(db.Database.HasPendingModelChanges());
            var migrations = db.Database.GetMigrations().ToArray();
            var legacy = migrations.Single(name => name.EndsWith("_MediaAndPush", StringComparison.Ordinal));
            var scanning = migrations.Single(name => name.EndsWith("_VerificationDocumentScanning", StringComparison.Ordinal));
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(legacy, timeout.Token);
            using var partial = await client.GetAsync("/health/ready", timeout.Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, partial.StatusCode);

            var draftId = Guid.NewGuid();
            var documentId = Guid.NewGuid();
            var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
            var content = "%PDF-audit fixture"u8.ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "ApplicationDrafts" ("Id", "TokenHash", "ExpiresAt")
                VALUES ({draftId}, {"legacy-draft"}, {expiresAt});
                INSERT INTO "VerificationDocuments" ("Id", "DraftId", "ContentType", "Size", "Content")
                VALUES ({documentId}, {draftId}, {"application/pdf"}, {content.Length}, {content});
                """, timeout.Token);
            await migrator.MigrateAsync(scanning, timeout.Token);
            using var missingWorkerMigration = await client.GetAsync("/health/ready", timeout.Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, missingWorkerMigration.StatusCode);
            var document = await db.VerificationDocuments.SingleAsync(row => row.Id == documentId, timeout.Token);
            Assert.Equal(VerificationScanStatus.PENDING, document.ScanStatus);

            var workers = migrations.Single(name => name.EndsWith("_WorkerReliability", StringComparison.Ordinal));
            await migrator.MigrateAsync(workers, timeout.Token);
            var user = Guid.NewGuid();
            var wrongUser = Guid.NewGuid();
            var company = Guid.NewGuid();
            var conversation = Guid.NewGuid();
            var wrongConversation = Guid.NewGuid();
            var message = Guid.NewGuid();
            var evt = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Users" ("Id", "Kind", "Email", "NormalizedEmail", "DisplayName", "PasswordHash", "CreatedAt", "IsActive")
                VALUES ({user}, 'CLIENT', 'legacy@example.test', 'LEGACY@EXAMPLE.TEST', 'Legacy', '', {now}, true),
                       ({wrongUser}, 'BUSINESS_MEMBER', 'wrong@example.test', 'WRONG@EXAMPLE.TEST', 'Wrong', '', {now}, true);
                INSERT INTO "Businesses" ("Id", "LegalName", "DisplayName", "RegistrationNumber", "Country", "Category", "Status", "IsActive", "CreatedAt")
                VALUES ({company}, 'Legacy', 'Legacy', 'LEGACY', 'KZ', 'Test', 'VERIFIED', true, {now});
                INSERT INTO "Conversations" ("Id", "ClientUserId", "BusinessId", "CreatedAt", "UpdatedAt")
                VALUES ({conversation}, {user}, {company}, {now}, {now}), ({wrongConversation}, {wrongUser}, {company}, {now}, {now});
                INSERT INTO "Messages" ("Id", "ConversationId", "AuthorUserId", "AuthorSide", "ClientMessageId", "Type", "Text", "CreatedAt")
                VALUES ({message}, {conversation}, {user}, 'CLIENT', {Guid.NewGuid()}, 'TEXT', 'legacy canonical body', {now});
                INSERT INTO "MessageReadReceipts" ("ConversationId", "Side", "LastMessageId", "LastMessageAt", "UpdatedAt")
                VALUES ({conversation}, 'CLIENT', {message}, {now}, {now});
                INSERT INTO "OutboxMessages" ("Id", "ConversationId", "EventName", "Payload", "OccurredAt", "AvailableAt", "CompletedAt", "Attempts")
                VALUES ({evt}, {conversation}, 'MessageCreated',
                        jsonb_build_object('message', jsonb_build_object('id', CAST({message} AS text), 'text', 'legacy copy')),
                        {now}, {now}, {now}, 1);
                """, timeout.Token);
            var invalid = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync(timeout.Token));
            Assert.Contains("Preflight integrity violations", invalid.MessageText);
            Assert.Equal(workers, (await db.Database.GetAppliedMigrationsAsync(timeout.Token)).Last());
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"Id\" = {wrongConversation}", timeout.Token);
            await db.Database.MigrateAsync(timeout.Token);
            Assert.Equal("legacy canonical body", await db.Messages.Where(row => row.Id == message).Select(row => row.Text).SingleAsync(timeout.Token));
            Assert.True(await db.MessageReadReceipts.AnyAsync(row => row.ConversationId == conversation && row.LastMessageId == message, timeout.Token));
            var payload = await db.OutboxMessages.Where(row => row.Id == evt).Select(row => row.Payload).SingleAsync(timeout.Token);
            Assert.DoesNotContain("legacy copy", payload);
            Assert.Contains(message.ToString("D"), payload);
            await migrator.MigrateAsync(migrations[0], timeout.Token);
            Assert.Equal(migrations[0], Assert.Single(await db.Database.GetAppliedMigrationsAsync(timeout.Token)));
            await db.Database.MigrateAsync(timeout.Token);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync(timeout.Token));
        }

        using var unconfigured = factory.CreateClient();
        using var missingKey = await unconfigured.GetAsync("/health/ready", timeout.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, missingKey.StatusCode);
        using var ready = await client.GetAsync("/health/ready", timeout.Token);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        using var production = configured.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        using var productionClient = production.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        using var missingProductionServices = await productionClient.GetAsync("/health/ready", timeout.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, missingProductionServices.StatusCode);

        await production.DisposeAsync();
        using var productionConfigured = configured.WithWebHostBuilder(builder => builder.UseEnvironment("Production")
            .UseSetting("AccountEmail:Host", "localhost")
            .UseSetting("AccountEmail:QueueKeyBase64", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))
            .UseSetting("FileScan:Host", "localhost")
            .UseSetting("ObjectStorage:ServiceUrl", "https://storage.example.test")
            .UseSetting("ObjectStorage:AccessKey", "test-access")
            .UseSetting("ObjectStorage:SecretKey", "test-secret"));
        using var productionConfiguredClient = productionConfigured.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        using var productionReady = await productionConfiguredClient.GetAsync("/health/ready", timeout.Token);
        Assert.Equal(HttpStatusCode.OK, productionReady.StatusCode);
    }
}
