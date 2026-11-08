using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using chat.Api.Messaging;
using chat.Application.Media;
using chat.Contracts;
using chat.Contracts.Businesses;
using chat.Contracts.Identity;
using chat.Contracts.Messaging;
using chat.Domain.Businesses;
using chat.Domain.Identity;
using chat.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace chat.IntegrationTests;

public sealed class BusinessMessagingTests(BackendFixture backend) : IClassFixture<BackendFixture>
{
    private const string Password = "integration-test-password";
    private const string PdfMime = "application/pdf";
    private static readonly byte[] Pdf = VerificationFiles.Pdf();

    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task ModerationCatalogMessagingAndDeliveryEnforceLivePermissions()
    {
        using var anonymous = backend.Host.CreateClient();
        using var admin = await CreateActorAsync(UserKind.PLATFORM_ADMIN);
        using var client = await CreateActorAsync(UserKind.CLIENT);
        using var outsider = await CreateActorAsync(UserKind.CLIENT);
        using var foreignStaff = await CreateActorAsync(UserKind.BUSINESS_MEMBER);
        await SeedForeignMembershipAsync(foreignStaff);
        var (application, documentId, ownerEmail) = await ApplyAsync(anonymous);
        using var owner = await SignInAsync(ownerEmail);

        Assert.Equal("PENDING_VERIFICATION", application.Status);
        Assert.Equal("PENDING_VERIFICATION", (await owner.GetFromJsonAsync<BusinessProfile>("/v1/my-business"))?.Status);
        await CheckStatusAsync(client.GetAsync($"/v1/businesses/{application.BusinessId}"), HttpStatusCode.NotFound);
        await CheckStatusAsync(owner.PatchAsJsonAsync("/v1/my-business", new UpdateBusinessRequest("Changed", null, null)),
            HttpStatusCode.Forbidden);
        var appUrl = $"/v1/admin/business-applications/{application.Id}";
        await CheckStatusAsync(owner.GetAsync($"{appUrl}/documents/{documentId}"), HttpStatusCode.Forbidden);
        await WaitForScanAsync(documentId, VerificationScanStatus.READY);
        Assert.Equal(Pdf, await admin.GetByteArrayAsync($"{appUrl}/documents/{documentId}"));
        var decision = new DecisionRequest("APPROVE", "Reviewed");
        await CheckStatusAsync(client.PostAsJsonAsync($"{appUrl}/decision", decision), HttpStatusCode.Forbidden);
        var results = await Task.WhenAll(admin.PostAsJsonAsync($"{appUrl}/decision", decision),
            admin.PostAsJsonAsync($"{appUrl}/decision", decision));
        try
        {
            Assert.Single(results, result => result.StatusCode == HttpStatusCode.OK);
            Assert.Single(results, result => result.StatusCode == HttpStatusCode.Conflict);
        }
        finally
        {
            foreach (var result in results) result.Dispose();
        }

        var card = await client.GetFromJsonAsync<BusinessCard>($"/v1/businesses/{application.BusinessId}");
        Assert.Equal("City Repair", card?.DisplayName);
        var search = await client.GetFromJsonAsync<PageResponse<BusinessCard>>("/v1/businesses?q=repair&limit=1");
        Assert.Equal(application.BusinessId, Assert.Single(search!.Items).Id);
        await CheckStatusAsync(owner.GetAsync("/v1/businesses"), HttpStatusCode.Forbidden);

        var create = new CreateConversationRequest(application.BusinessId);
        var chats = await Task.WhenAll(PostAsync<ConversationView>(client, "/v1/conversations", create),
            PostAsync<ConversationView>(client, "/v1/conversations", create));
        Assert.Equal(chats[0].Id, chats[1].Id);
        await CheckStatusAsync(owner.PostAsJsonAsync("/v1/conversations", create), HttpStatusCode.Forbidden);
        var id = chats[0].Id;
        var chatUrl = $"/v1/conversations/{id}";
        Assert.Null(chats[0].ReadState.Client);
        Assert.Null(chats[0].ReadState.Business);
        await CheckStatusAsync(outsider.GetAsync(chatUrl), HttpStatusCode.NotFound);
        await CheckStatusAsync(foreignStaff.GetAsync(chatUrl), HttpStatusCode.NotFound);
        await CheckStatusAsync(outsider.GetAsync($"{chatUrl}/messages"), HttpStatusCode.NotFound);
        await CheckStatusAsync(foreignStaff.GetAsync($"{chatUrl}/messages"), HttpStatusCode.NotFound);
        await CheckStatusAsync(outsider.PostAsJsonAsync($"{chatUrl}/messages",
            new SendMessageRequest(Guid.NewGuid(), "TEXT", "Intrusion", null)), HttpStatusCode.NotFound);

        var delivered = Channel.CreateUnbounded<EventEnvelope<JsonElement>>();
        await using var ownerHub = CreateHub(owner);
        await using var outsiderHub = CreateHub(outsider);
        using var subscription = ownerHub.On<EventEnvelope<JsonElement>>("MessageCreated",
            evt => delivered.Writer.TryWrite(evt));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await ownerHub.StartAsync(timeout.Token);
        await ownerHub.InvokeAsync("JoinConversation", id.ToString("D"), timeout.Token);
        await outsiderHub.StartAsync(timeout.Token);
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(
            () => outsiderHub.InvokeAsync("JoinConversation", id.ToString("D"), timeout.Token));
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(
            () => outsiderHub.InvokeAsync("LeaveConversation", id.ToString("D"), timeout.Token));
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(
            () => outsiderHub.InvokeAsync("SetTyping", id.ToString("D"), true, timeout.Token));
        await using (var clientHub = CreateHub(client))
        {
            var typing = new TaskCompletionSource<EventEnvelope<JsonElement>>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var typingSubscription = ownerHub.On<EventEnvelope<JsonElement>>("TypingChanged", evt => typing.TrySetResult(evt));
            await clientHub.StartAsync(timeout.Token);
            await clientHub.InvokeAsync("JoinConversation", id.ToString("D"), timeout.Token);
            await clientHub.InvokeAsync("SetTyping", id.ToString("D"), true, timeout.Token);
            var evt = await typing.Task.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
            Assert.Equal(id, evt.ConversationId);
            Assert.Equal("CLIENT", evt.Payload.GetProperty("side").GetString());
            Assert.True(evt.Payload.GetProperty("isTyping").GetBoolean());
            await clientHub.InvokeAsync("LeaveConversation", id.ToString("D"), timeout.Token);
        }

        const string pushToken = "ExponentPushToken[test-device]";
        var device = await PostAsync<RegisteredDevice>(owner, "/v1/devices", new RegisterDeviceRequest(pushToken));
        await CheckStatusAsync(outsider.DeleteAsync($"/v1/devices/{device.Id}"), HttpStatusCode.NotFound);

        var request = new SendMessageRequest(Guid.NewGuid(), "TEXT", "Hello", null);
        var resumePush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Push.BeforeResult = ct => resumePush.Task.WaitAsync(ct);
        var sent = await Task.WhenAll(PostAsync<MessageView>(client, $"{chatUrl}/messages", request),
            PostAsync<MessageView>(client, $"{chatUrl}/messages", request));
        Assert.Equal(sent[0].Id, sent[1].Id);
        Assert.Equal(sent[0].CreatedAt, sent[1].CreatedAt);
        var received = await delivered.Reader.ReadAsync(timeout.Token);
        Assert.Equal(id, received.ConversationId);
        Assert.Equal(1, received.EventVersion);
        Assert.Equal(sent[0].Id, received.Payload.GetProperty("message").GetProperty("id").GetGuid());
        Assert.False(received.Payload.GetProperty("message").TryGetProperty("authorUserId", out _));
        var firstPush = await backend.Push.Sends.Reader.ReadAsync(timeout.Token);
        try
        {
            // PostgreSQL must be able to commit a real ACL change while Expo is stalled.
            await SetMembershipStatusAsync(application.BusinessId, false).WaitAsync(TimeSpan.FromSeconds(3));
            await SetMembershipStatusAsync(application.BusinessId, true).WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            backend.Push.BeforeResult = null;
            resumePush.TrySetResult();
        }
        var retriedPush = await backend.Push.Sends.Reader.ReadAsync(timeout.Token);
        Assert.Equal((pushToken, received.EventId, id), firstPush);
        Assert.Equal(firstPush, retriedPush);
        await CheckStatusAsync(owner.DeleteAsync($"/v1/devices/{device.Id}"), HttpStatusCode.NoContent);

        var reply = await PostAsync<MessageView>(owner, $"{chatUrl}/messages",
            new SendMessageRequest(Guid.NewGuid(), "TEXT", "We can help", null));
        Assert.Equal("BUSINESS", reply.AuthorSide);
        var page = (await client.GetFromJsonAsync<PageResponse<MessageView>>($"{chatUrl}/messages?limit=1"))!;
        Assert.Equal(reply.Id, Assert.Single(page.Items).Id);
        Assert.NotNull(page.NextCursor);
        var older = (await client.GetFromJsonAsync<PageResponse<MessageView>>($"{chatUrl}/messages?limit=1&cursor={page.NextCursor}"))!;
        Assert.Equal(sent[0].Id, Assert.Single(older.Items).Id);
        await ownerHub.StopAsync(timeout.Token);
        var businessRead = await PostAsync<ReadReceiptView>(owner, $"{chatUrl}/read",
            new ReadConversationRequest(sent[0].Id));
        var read = await PostAsync<ReadReceiptView>(client, $"{chatUrl}/read", new ReadConversationRequest(reply.Id));
        Assert.Equal(reply.Id, read.LastMessageId);
        var staleRead = await PostAsync<ReadReceiptView>(client, $"{chatUrl}/read", new ReadConversationRequest(sent[0].Id));
        Assert.Equal(read, staleRead);
        await ownerHub.StartAsync(timeout.Token);
        await ownerHub.InvokeAsync("JoinConversation", id.ToString("D"), timeout.Token);

        var expected = new ConversationReadState(new ReadPosition(read.LastMessageId, read.UpdatedAt),
            new ReadPosition(businessRead.LastMessageId, businessRead.UpdatedAt));
        await AssertReadStateAsync(client, id, expected);
        await AssertReadStateAsync(owner, id, expected);

        var otherChat = await PostAsync<ConversationView>(outsider, "/v1/conversations", create);
        var otherMessage = await PostAsync<MessageView>(outsider, $"/v1/conversations/{otherChat.Id}/messages",
            new SendMessageRequest(Guid.NewGuid(), "TEXT", "Different conversation", null));
        await AssertOfflineDispatchAsync(otherChat.Id, timeout.Token);
        await CheckStatusAsync(client.PostAsJsonAsync($"{chatUrl}/read", new ReadConversationRequest(otherMessage.Id)),
            HttpStatusCode.NotFound);
        await CheckStatusAsync(foreignStaff.PostAsJsonAsync($"{chatUrl}/read", new ReadConversationRequest(reply.Id)),
            HttpStatusCode.NotFound);

        var readEvents = Channel.CreateUnbounded<EventEnvelope<JsonElement>>();
        using var readSubscription = ownerHub.On<EventEnvelope<JsonElement>>("ConversationReadUpdated",
            evt => readEvents.Writer.TryWrite(evt));
        var advance = PostAsync<ReadReceiptView>(owner, $"{chatUrl}/read", new ReadConversationRequest(reply.Id));
        var newMessage = PostAsync<MessageView>(client, $"{chatUrl}/messages",
            new SendMessageRequest(Guid.NewGuid(), "TEXT", "Sent while reading", null));
        await Task.WhenAll(advance, newMessage);
        var advanced = await advance;
        Assert.Equal(reply.Id, advanced.LastMessageId);
        Assert.True(advanced.UpdatedAt > businessRead.UpdatedAt);
        EventEnvelope<JsonElement> readEvent;
        do
        {
            readEvent = await readEvents.Reader.ReadAsync(timeout.Token);
        }
        while (readEvent.Payload.GetProperty("readerSide").GetString() != "BUSINESS"
            || readEvent.Payload.GetProperty("lastMessageId").GetGuid() != advanced.LastMessageId);
        Assert.Equal(id, readEvent.ConversationId);
        Assert.Equal(advanced.UpdatedAt, readEvent.Payload.GetProperty("updatedAt").GetDateTimeOffset());
        await AssertReadStateAsync(owner, id,
            expected with { Business = new ReadPosition(advanced.LastMessageId, advanced.UpdatedAt) });

        var mislabeled = "not a pdf"u8.ToArray();
        var quarantined = await PostAsync<PresignedUpload>(client, "/v1/uploads/presign",
            new PresignUploadRequest(id, PdfMime, mislabeled.Length));
        await backend.Storage.UploadAsync(quarantined, mislabeled, PdfMime);
        using var quarantine = await client.PostAsync($"/v1/uploads/{quarantined.UploadId}/complete", null);
        quarantine.EnsureSuccessStatusCode();
        Assert.Equal("QUARANTINED", (await quarantine.Content.ReadFromJsonAsync<CompletedUpload>())?.Status);
        await CheckStatusAsync(client.GetAsync($"/v1/attachments/{quarantined.UploadId}/download"), HttpStatusCode.NotFound);

        var upload = await PostAsync<PresignedUpload>(client, "/v1/uploads/presign", new PresignUploadRequest(id, PdfMime, Pdf.Length));
        Assert.InRange(upload.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
        await CheckStatusAsync(owner.PostAsync($"/v1/uploads/{upload.UploadId}/complete", null), HttpStatusCode.NotFound);
        await backend.Storage.UploadAsync(upload, Pdf, PdfMime);
        using var complete = await client.PostAsync($"/v1/uploads/{upload.UploadId}/complete", null);
        complete.EnsureSuccessStatusCode();
        Assert.Equal("READY", (await complete.Content.ReadFromJsonAsync<CompletedUpload>())?.Status);
        await CheckStatusAsync(owner.PostAsJsonAsync($"{chatUrl}/messages",
            new SendMessageRequest(Guid.NewGuid(), "FILE", null, [upload.UploadId])), HttpStatusCode.Conflict);
        var attachmentMessage = await PostAsync<MessageView>(client, $"{chatUrl}/messages",
            new SendMessageRequest(Guid.NewGuid(), "FILE", null, [upload.UploadId]));
        Assert.Equal(upload.UploadId, Assert.Single(attachmentMessage.Attachments).Id);
        await CheckStatusAsync(outsider.GetAsync($"/v1/attachments/{upload.UploadId}/download"), HttpStatusCode.NotFound);
        var download = (await owner.GetFromJsonAsync<AttachmentDownload>($"/v1/attachments/{upload.UploadId}/download"))!;
        Assert.InRange(download.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromSeconds(50), TimeSpan.FromMinutes(1));
        Assert.Equal(Pdf, await backend.Storage.DownloadAsync(download));

        var stale = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        outsiderHub.Closed += _ => { stale.TrySetResult(); return Task.CompletedTask; };
        await VerifyMediaCleanupAsync(client, outsider, id, otherChat.Id, upload, quarantined, download);

        var membershipRevoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ownerHub.Closed += _ => { membershipRevoked.TrySetResult(); return Task.CompletedTask; };
        await SetMembershipStatusAsync(application.BusinessId, false);
        await membershipRevoked.Task.WaitAsync(TimeSpan.FromSeconds(6), timeout.Token);
        await CheckStatusAsync(owner.GetAsync($"{chatUrl}/messages"), HttpStatusCode.NotFound);
        await SetMembershipStatusAsync(application.BusinessId, true);
        await ownerHub.StartAsync(timeout.Token);
        await ownerHub.InvokeAsync("JoinConversation", id.ToString("D"), timeout.Token);

        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var revokedSubscription = ownerHub.On<EventEnvelope<JsonElement>>("BusinessAccessRevoked", _ => leaked.TrySetResult());
        ownerHub.Closed += _ => { disconnected.TrySetResult(); return Task.CompletedTask; };
        await CheckStatusAsync(admin.PostAsJsonAsync($"/v1/admin/businesses/{application.BusinessId}/suspend",
            new SuspendBusinessRequest("Moderation test")), HttpStatusCode.NoContent);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(6), timeout.Token);
        Assert.False(leaked.Task.IsCompleted);
        await CheckStatusAsync(client.GetAsync($"/v1/businesses/{application.BusinessId}"), HttpStatusCode.NotFound);
        await CheckStatusAsync(owner.GetAsync($"{chatUrl}/messages"), HttpStatusCode.NotFound);
        await CheckStatusAsync(client.GetAsync($"/v1/attachments/{upload.UploadId}/download"), HttpStatusCode.NotFound);
        Assert.Equal(Pdf, await backend.Storage.DownloadAsync(download));

        await stale.Task.WaitAsync(timeout.Token);
        await Assert.ThrowsAnyAsync<Exception>(() => outsiderHub.StartAsync(timeout.Token));
        Assert.False(timeout.IsCancellationRequested);
    }

    private async Task AssertOfflineDispatchAsync(Guid id, CancellationToken ct)
    {
        await using var scope = backend.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        while (!await db.OutboxMessages.AnyAsync(row => row.ConversationId == id && row.EventName == Outbox.MessageCreated
                   && row.CompletedAt != null && row.Outcome == "offline", ct))
        {
            await Task.Delay(100, ct);
        }
    }

    private async Task VerifyMediaCleanupAsync(HttpClient client, HttpClient outsider, Guid id, Guid otherId,
        PresignedUpload attached, PresignedUpload quarantined, AttachmentDownload download)
    {
        var pending = await PostAsync<PresignedUpload>(client, "/v1/uploads/presign", new PresignUploadRequest(id, PdfMime, Pdf.Length));
        await backend.Storage.UploadAsync(pending, Pdf, PdfMime);
        var unattached = await PostAsync<PresignedUpload>(client, "/v1/uploads/presign", new PresignUploadRequest(id, PdfMime, Pdf.Length));
        await backend.Storage.UploadAsync(unattached, Pdf, PdfMime);
        var completes = await Task.WhenAll(client.PostAsync($"/v1/uploads/{unattached.UploadId}/complete", null),
            client.PostAsync($"/v1/uploads/{unattached.UploadId}/complete", null));
        foreach (var response in completes)
        {
            using (response)
            {
                response.EnsureSuccessStatusCode();
                Assert.Equal("READY", (await response.Content.ReadFromJsonAsync<CompletedUpload>())?.Status);
            }
        }

        var discarded = await PostAsync<PresignedUpload>(outsider, "/v1/uploads/presign", new PresignUploadRequest(otherId, PdfMime, Pdf.Length));
        await backend.Storage.UploadAsync(discarded, Pdf, PdfMime);
        var outsiderEmail = (await outsider.GetFromJsonAsync<MeResponse>("/v1/me"))!.Email;
        await CheckStatusAsync(outsider.PostAsync("/v1/auth/logout", null), HttpStatusCode.NoContent);
        await CheckStatusAsync(outsider.PostAsync($"/v1/uploads/{discarded.UploadId}/complete", null), HttpStatusCode.Unauthorized);
        using var reauthenticated = await SignInAsync(outsiderEmail);
        await CheckStatusAsync(reauthenticated.PostAsync($"/v1/uploads/{discarded.UploadId}/complete", null), HttpStatusCode.NotFound);
        Assert.NotNull(await reauthenticated.GetFromJsonAsync<ConversationView>($"/v1/conversations/{otherId}"));

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CHAT_TEST_CLAMAV_HOST")))
        {
            var blocked = VerificationFiles.Pdf("scan-unavailable");
            var retry = await PostAsync<PresignedUpload>(client, "/v1/uploads/presign", new PresignUploadRequest(id, PdfMime, blocked.Length));
            await backend.Storage.UploadAsync(retry, blocked, PdfMime);
            await CheckStatusAsync(client.PostAsync($"/v1/uploads/{retry.UploadId}/complete", null), HttpStatusCode.ServiceUnavailable);
            await CheckStatusAsync(client.GetAsync($"/v1/attachments/{retry.UploadId}/download"), HttpStatusCode.NotFound);
        }

        await using var scope = backend.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IPrivateObjectStorage>();
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE "Attachments" ADD CONSTRAINT "TestRejectReady" CHECK ("Status" <> 'READY') NOT VALID
            """);
        try
        {
            await CheckStatusAsync(client.PostAsync($"/v1/uploads/{pending.UploadId}/complete", null), HttpStatusCode.InternalServerError);
            Assert.Equal(Pdf, await storage.ReadUploadAsync(MediaService.VerifiedKey(pending.UploadId), PdfMime, Pdf.Length, default));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("""ALTER TABLE "Attachments" DROP CONSTRAINT "TestRejectReady" """);
        }

        var ids = new[] { pending.UploadId, unattached.UploadId, attached.UploadId, quarantined.UploadId, discarded.UploadId };
        var cleanup = backend.Host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<AttachmentCleanupWorker>().Single();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var failedKey = MediaService.VerifiedKey(pending.UploadId);
        var failureObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CHAT_TEST_S3_URL")))
        {
            backend.Storage.FailDelete = key =>
            {
                if (key != failedKey)
                {
                    return false;
                }

                failureObserved.TrySetResult();
                return true;
            };
        }

        try
        {
            await db.Attachments.Where(row => ids.Contains(row.Id)).ExecuteUpdateAsync(update => update
                .SetProperty(row => row.UploadExpiresAt, DateTimeOffset.UtcNow.AddDays(-2)), timeout.Token);
            while (backend.Storage.FailDelete is not null && !failureObserved.Task.IsCompleted)
            {
                if (!await cleanup.CleanNextAsync(timeout.Token))
                {
                    await Task.Delay(50, timeout.Token);
                }
            }
        }
        finally
        {
            backend.Storage.FailDelete = null;
        }

        if (failureObserved.Task.IsCompleted)
        {
            Assert.True(await db.Attachments.AnyAsync(row => row.Id == pending.UploadId && row.RetiredAt != null
                && row.CleanupAttempts > 0 && row.ObjectsDeletedAt == null, timeout.Token));
            await CheckStatusAsync(client.PostAsync($"/v1/uploads/{pending.UploadId}/complete", null), HttpStatusCode.NotFound);
            Assert.Equal(Pdf, await storage.ReadUploadAsync(failedKey, PdfMime, Pdf.Length, timeout.Token));
            await db.Attachments.Where(row => row.Id == pending.UploadId).ExecuteUpdateAsync(update => update
                .SetProperty(row => row.CleanupAfter, DateTimeOffset.UtcNow.AddSeconds(-1)), timeout.Token);
        }

        while (await db.Attachments.AnyAsync(row => ids.Contains(row.Id) && row.ObjectsDeletedAt == null, timeout.Token))
        {
            if (!await cleanup.CleanNextAsync(timeout.Token))
            {
                await Task.Delay(100, timeout.Token);
            }
        }

        foreach (var item in ids.Where(item => item != attached.UploadId))
        {
            Assert.True(await db.Attachments.AnyAsync(row => row.Id == item && row.RetiredAt != null));
            Assert.Null(await storage.ReadUploadAsync(MediaService.VerifiedKey(item), PdfMime, Pdf.Length, timeout.Token));
        }

        Assert.False(await db.Attachments.AnyAsync(row => row.Id == attached.UploadId && row.RetiredAt != null));
        Assert.Equal(Pdf, await backend.Storage.DownloadAsync(download));
        Assert.Null(await storage.ReadUploadAsync(MediaService.StagingKey(attached.UploadId), PdfMime, Pdf.Length, timeout.Token));
    }

    private static async Task AssertReadStateAsync(HttpClient actor, Guid id, ConversationReadState expected)
    {
        var detail = await actor.GetFromJsonAsync<ConversationView>($"/v1/conversations/{id}");
        var page = await actor.GetFromJsonAsync<PageResponse<ConversationView>>("/v1/conversations");
        Assert.Equal(expected, detail!.ReadState);
        Assert.Equal(expected, Assert.Single(page!.Items, item => item.Id == id).ReadState);
    }

    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task DraftUploadRuntimeMatchesOpenApiWithPostgres()
    {
        using var client = backend.Host.CreateClient();
        var document = JsonNode.Parse(await client.GetStringAsync("/openapi/v1.json"))!;
        var upload = document["paths"]!["/v1/business-applications/drafts/{id}/documents"]!["post"]!;
        var header = Assert.Single(upload["parameters"]!.AsArray(),
            parameter => (string?)parameter?["name"] == "X-Application-Draft")!;
        Assert.True((bool)header["required"]!);
        var draft = await PostAsync<BusinessDraftResponse>(client, "/v1/business-applications/drafts", new { });

        using var missingHeader = CreateDocumentRequest(draft.DraftId, draft.ApplicationDraftToken, Pdf);
        missingHeader.Headers.Remove("X-Application-Draft");
        await AssertDocumentedAsync(missingHeader, HttpStatusCode.NotFound, "NOT_FOUND");
        using var unsupported = CreateDocumentRequest(draft.DraftId, draft.ApplicationDraftToken, Pdf);
        unsupported.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        await AssertDocumentedAsync(unsupported, HttpStatusCode.UnsupportedMediaType, "VALIDATION_ERROR");
        using var tooLarge = CreateDocumentRequest(draft.DraftId, draft.ApplicationDraftToken, new byte[5 * 1024 * 1024 + 1]);
        await AssertDocumentedAsync(tooLarge, HttpStatusCode.RequestEntityTooLarge, "VALIDATION_ERROR");

        using var valid = CreateDocumentRequest(draft.DraftId, draft.ApplicationDraftToken, Pdf);
        using var response = await client.SendAsync(valid);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(upload["responses"]?["200"]);
        var file = await response.Content.ReadFromJsonAsync<VerificationDocumentInfo>();
        Assert.Equal("PENDING", file!.ScanStatus);
        var states = document["components"]!["schemas"]!["VerificationDocumentInfo"]!["properties"]!["scanStatus"]!["enum"]!;
        Assert.Contains(file.ScanStatus, states.AsArray().Select(value => (string?)value));

        async Task AssertDocumentedAsync(HttpRequestMessage request, HttpStatusCode status, string code)
        {
            using var result = await client.SendAsync(request);
            Assert.Equal(status, result.StatusCode);
            var error = await result.Content.ReadFromJsonAsync<ErrorEnvelope>();
            Assert.Equal(code, error!.Error.Code);
            Assert.NotNull(upload["responses"]?[$"{(int)status}"]?["content"]?["application/json"]);
        }
    }

    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task VerificationDocumentsRequireFormatAndCleanScanBeforeDownloadOrApproval()
    {
        using var anonymous = backend.Host.CreateClient();
        using var admin = await CreateActorAsync(UserKind.PLATFORM_ADMIN);
        var (malformed, malformedId, _) = await ApplyAsync(anonymous,
            "%PDF- inert data without a PDF envelope"u8.ToArray(), "malformed");
        await WaitForScanAsync(malformedId, VerificationScanStatus.QUARANTINED);
        await AssertNotReviewableAsync(admin, malformed.Id, malformedId);

        // The scanner adapter returns the same verdict as a ClamAV FOUND reply in this test.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CHAT_TEST_CLAMAV_HOST")))
        {
            var (infected, infectedId, _) = await ApplyAsync(anonymous, VerificationFiles.Pdf("scan-denied"), "infected");
            await WaitForScanAsync(infectedId, VerificationScanStatus.QUARANTINED);
            await AssertNotReviewableAsync(admin, infected.Id, infectedId);

            var (pending, pendingId, _) = await ApplyAsync(anonymous, VerificationFiles.Pdf("scan-unavailable"), "pending");
            try
            {
                await WaitForScanAsync(pendingId, VerificationScanStatus.PENDING, requireRetry: true);
                await AssertNotReviewableAsync(admin, pending.Id, pendingId);
            }
            finally
            {
                await using var scope = backend.Host.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
                await db.VerificationDocuments.Where(row => row.Id == pendingId)
                    .ExecuteDeleteAsync();
            }
        }
    }

    private static async Task AssertNotReviewableAsync(HttpClient admin, Guid applicationId, Guid documentId)
    {
        var path = $"/v1/admin/business-applications/{applicationId}";
        await CheckStatusAsync(admin.GetAsync($"{path}/documents/{documentId}"), HttpStatusCode.NotFound);
        using var decision = await admin.PostAsJsonAsync($"{path}/decision", new DecisionRequest("APPROVE", null));
        Assert.Equal(HttpStatusCode.Conflict, decision.StatusCode);
        Assert.Equal("MEDIA_NOT_READY", (await decision.Content.ReadFromJsonAsync<ErrorEnvelope>())?.Error.Code);
    }

    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task BusinessClaimsAreProvisionalAndRejectedOwnerCanReapplyWithoutLosingHistory()
    {
        using var anonymous = backend.Host.CreateClient();
        using var admin = await CreateActorAsync(UserKind.PLATFORM_ADMIN);
        const string registration = "contested-claim";
        var (first, _, email) = await ApplyAsync(anonymous, registrationNumber: registration);
        var (challenger, challengeDocument, challengerEmail) = await ApplyAsync(anonymous, registrationNumber: registration);
        using var owner = await SignInAsync(email);
        using var stranger = await SignInAsync(challengerEmail);

        var firstPath = $"/v1/admin/business-applications/{first.Id}";
        _ = await PostAsync<BusinessApplicationReceipt>(admin, $"{firstPath}/decision", new DecisionRequest("REJECT", "Proof insufficient"));
        var (draft, document) = await UploadDraftAsync(anonymous);
        var retry = new ReapplyBusinessRequest([document.Id], draft.ApplicationDraftToken);
        var reapply = $"/v1/my-business/{first.BusinessId}/applications";
        await CheckStatusAsync(anonymous.PostAsJsonAsync(reapply, retry), HttpStatusCode.Unauthorized);
        await CheckStatusAsync(stranger.PostAsJsonAsync(reapply, retry), HttpStatusCode.NotFound);
        var retries = await Task.WhenAll(owner.PostAsJsonAsync(reapply, retry), owner.PostAsJsonAsync(reapply, retry));
        using var accepted = Assert.Single(retries, response => response.StatusCode == HttpStatusCode.OK);
        using var conflicting = Assert.Single(retries, response => response.StatusCode == HttpStatusCode.Conflict);
        var reapplied = (await accepted.Content.ReadFromJsonAsync<BusinessApplicationReceipt>())!;
        Assert.NotEqual(first.Id, reapplied.Id);
        Assert.Equal(first.BusinessId, reapplied.BusinessId);
        await CheckStatusAsync(admin.PostAsJsonAsync($"{firstPath}/decision", new DecisionRequest("APPROVE", null)), HttpStatusCode.Conflict);
        var archived = await admin.GetFromJsonAsync<BusinessApplicationDetails>(firstPath);
        Assert.Equal("REJECTED", archived?.Status);
        Assert.Equal("Proof insufficient", archived?.Reason);

        await WaitForScanAsync(document.Id, VerificationScanStatus.READY);
        await WaitForScanAsync(challengeDocument, VerificationScanStatus.READY);
        var approve = new DecisionRequest("APPROVE", "Evidence reviewed");
        var decisions = await Task.WhenAll(
            admin.PostAsJsonAsync($"/v1/admin/business-applications/{reapplied.Id}/decision", approve),
            admin.PostAsJsonAsync($"/v1/admin/business-applications/{challenger.Id}/decision", approve));
        using var winner = Assert.Single(decisions, response => response.StatusCode == HttpStatusCode.OK);
        using var denied = Assert.Single(decisions, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("CONFLICT", (await denied.Content.ReadFromJsonAsync<ErrorEnvelope>())?.Error.Code);

        var (duplicateDraft, duplicateDocument) = await UploadDraftAsync(anonymous);
        var duplicate = new SubmitBusinessRequest(new RegisterClientRequest(email, Password, "Impersonator"),
            new BusinessDetailsRequest("Legal Name", "Contested Repair", registration, "KZ", "Services", null),
            [duplicateDocument.Id], duplicateDraft.ApplicationDraftToken);
        var receipt = await PostAsync<BusinessApplicationReceipt>(anonymous, "/v1/business-applications", duplicate);
        Assert.Equal("PENDING_VERIFICATION", receipt.Status);
        await CheckStatusAsync(anonymous.PostAsJsonAsync("/v1/business-applications", duplicate), HttpStatusCode.NotFound);

        await using var scope = backend.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        Assert.False(await db.BusinessApplications.AnyAsync(row => row.Id == receipt.Id));
        Assert.Equal(1, await db.Businesses.CountAsync(row => row.RegistrationNumber == registration.ToUpperInvariant()
            && row.Status == BusinessStatus.VERIFIED));
        Assert.Equal(1, await db.AuditLogs.CountAsync(row => row.EntityId == first.Id && row.Action == "REJECT"));
        Assert.Equal(1, await db.AuditLogs.CountAsync(row => row.EntityId == reapplied.Id && row.Action == "REAPPLY"));
    }

    private static async Task<(BusinessDraftResponse Draft, VerificationDocumentInfo Document)> UploadDraftAsync(HttpClient client)
    {
        var draft = await PostAsync<BusinessDraftResponse>(client, "/v1/business-applications/drafts", new { });
        using var request = CreateDocumentRequest(draft.DraftId, draft.ApplicationDraftToken, Pdf);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (draft, (await response.Content.ReadFromJsonAsync<VerificationDocumentInfo>())!);
    }

    private async Task WaitForScanAsync(Guid documentId, VerificationScanStatus expected, bool requireRetry = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var scope = backend.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        while (!await db.VerificationDocuments.AnyAsync(row => row.Id == documentId && row.ScanStatus == expected
                       && (!requireRetry || row.ScanAfter != null), timeout.Token))
        {
            await Task.Delay(100, timeout.Token);
        }
    }

    private async Task SetMembershipStatusAsync(Guid businessId, bool active)
    {
        await using var scope = backend.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        await db.BusinessMemberships.Where(row => row.BusinessId == businessId)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.IsActive, active));
    }

    private async Task SeedForeignMembershipAsync(HttpClient actor)
    {
        var me = (await actor.GetFromJsonAsync<MeResponse>("/v1/me"))!;
        await using var scope = backend.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var business = new Business
        {
            LegalName = "Other Business",
            DisplayName = "Other Business",
            RegistrationNumber = "0002",
            Country = "KZ",
            Category = "Other",
            Status = BusinessStatus.VERIFIED,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.AddRange(business, new BusinessMembership { BusinessId = business.Id, UserId = me.Id, Role = BusinessRole.AGENT });
        await db.SaveChangesAsync();
    }

    private async Task<(BusinessApplicationReceipt Application, Guid DocumentId, string Email)> ApplyAsync(HttpClient anonymous, byte[]? content = null, string registrationNumber = "0001")
    {
        var draft = await PostAsync<BusinessDraftResponse>(anonymous, "/v1/business-applications/drafts", new { });
        var other = await PostAsync<BusinessDraftResponse>(anonymous, "/v1/business-applications/drafts", new { });
        using var forbidden = CreateDocumentRequest(draft.DraftId, other.ApplicationDraftToken, content ?? Pdf);
        await CheckStatusAsync(anonymous.SendAsync(forbidden), HttpStatusCode.NotFound);
        using var upload = CreateDocumentRequest(draft.DraftId, draft.ApplicationDraftToken, content ?? Pdf);
        using var response = await anonymous.SendAsync(upload);
        response.EnsureSuccessStatusCode();
        var document = (await response.Content.ReadFromJsonAsync<VerificationDocumentInfo>())!;
        var email = $"{Guid.NewGuid():N}@example.test";
        var request = new SubmitBusinessRequest(new RegisterClientRequest(email, Password, "Owner"),
            new BusinessDetailsRequest("Legal Name", "City Repair", registrationNumber, "KZ", "Services", null),
            [document.Id], draft.ApplicationDraftToken);
        await CheckStatusAsync(anonymous.PostAsJsonAsync("/v1/business-applications",
            request with { ApplicationDraftToken = other.ApplicationDraftToken }), HttpStatusCode.NotFound);
        var application = await PostAsync<BusinessApplicationReceipt>(anonymous, "/v1/business-applications", request);
        var code = await backend.ReadCodeAsync("chat email verification");
        await CheckStatusAsync(anonymous.PostAsJsonAsync("/v1/auth/email/verify", new VerifyEmailRequest(code)), HttpStatusCode.NoContent);
        return (application, document.Id, email);
    }

    private static HttpRequestMessage CreateDocumentRequest(Guid id, string token, byte[] bytes)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/business-applications/drafts/{id}/documents")
        {
            Content = new ByteArrayContent(bytes)
        };
        request.Headers.Add("X-Application-Draft", token);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(PdfMime);
        return request;
    }

    private async Task<HttpClient> CreateActorAsync(UserKind kind)
    {
        await using var scope = backend.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var email = $"{Guid.NewGuid():N}@example.test";
        var user = new User
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = kind.ToString(),
            Kind = kind,
            EmailVerifiedAt = DateTimeOffset.UtcNow
        };
        user.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>().HashPassword(user, Password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return await SignInAsync(email);
    }

    private async Task<HttpClient> SignInAsync(string email)
    {
        var client = backend.Host.CreateClient();
        var tokens = await PostAsync<AuthTokens>(client, "/v1/auth/login", new LoginRequest(email, Password));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private HubConnection CreateHub(HttpClient actor) =>
        new HubConnectionBuilder().WithUrl(new Uri("https://localhost/hubs/messaging"), options =>
        {
            options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
            options.SkipNegotiation = true;
            options.AccessTokenProvider = () => Task.FromResult(actor.DefaultRequestHeaders.Authorization?.Parameter);
            options.WebSocketFactory = async (context, ct) =>
            {
                var socket = backend.Host.Server.CreateWebSocketClient();
                socket.ConfigureRequest = request =>
                {
                    request.Headers.Authorization = actor.DefaultRequestHeaders.Authorization?.ToString();
                };
                return await socket.ConnectAsync(context.Uri, ct);
            };
        }).Build();

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object request)
    {
        using var response = await client.PostAsJsonAsync(url, request);
        Assert.True(response.IsSuccessStatusCode,
            $"{url} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task CheckStatusAsync(Task<HttpResponseMessage> pending, HttpStatusCode expected)
    {
        using var response = await pending;
        Assert.Equal(expected, response.StatusCode);
    }
}
