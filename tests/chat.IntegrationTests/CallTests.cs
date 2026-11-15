using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using chat.Api.Calls;
using chat.Contracts.Calls;
using chat.Contracts.Identity;
using chat.Contracts.Messaging;
using chat.Domain.Businesses;
using chat.Domain.Calls;
using chat.Domain.Identity;
using chat.Domain.Messaging;
using chat.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

namespace chat.IntegrationTests;

public sealed class CallTests(BackendFixture backend) : IClassFixture<BackendFixture>
{
    private const string Password = "b06-integration-password";

    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task JavaScriptConsumerUsesRealHttpsAndWss()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        // Schannel requires an imported certificate key for the real TLS listener on Windows.
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
        // A separate database and a single host keep the B05 single-replica/outbox architecture intact.
        var network = new BackendFixture { NetworkCertificate = certificate };
        await network.InitializeAsync();
        var room = await SeedAsync(network);
        using var client = room.Client;
        using var staff = room.Staff;
        using var other = room.Other;
        using var foreign = room.Foreign;
        var address = network.Host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var trust = Path.Combine(Path.GetTempPath(), $"chat-b06-{Guid.NewGuid():N}.pem");
        try
        {
            await File.WriteAllTextAsync(trust, certificate.ExportCertificatePem());
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("tools/consumer-smoke/b06-smoke.mjs");
            start.Environment["NODE_EXTRA_CA_CERTS"] = trust;
            start.Environment["CHAT_B06_API"] = address;
            start.Environment["CHAT_B06_ROOM"] = room.Id.ToString();
            start.Environment["CHAT_B06_CLIENT"] = client.DefaultRequestHeaders.Authorization!.Parameter;
            start.Environment["CHAT_B06_STAFF"] = staff.DefaultRequestHeaders.Authorization!.Parameter;
            start.Environment["CHAT_B06_OTHER"] = other.DefaultRequestHeaders.Authorization!.Parameter;
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            Assert.True(process.ExitCode == 0, $"JS consumer failed: {await stdout} {await stderr}");
            Assert.Contains("B06 JS HTTPS/WSS passed", await stdout);
            Assert.DoesNotContain("failed", await stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(trust); await network.DisposeAsync(); }
    }

    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task TimedMediaUsesRealProbeAndPreservesExistingFileContracts()
    {
        var room = await SeedAsync();
        using var client = room.Client;
        using var staff = room.Staff;
        using var other = room.Other;
        using var foreign = room.Foreign;
        var path = Path.Combine(Path.GetTempPath(), $"chat-b06-{Guid.NewGuid():N}.mp4");
        var probe = Environment.GetEnvironmentVariable("CHAT_TEST_FFPROBE") ?? "ffprobe";
        var ffmpeg = Path.IsPathRooted(probe) ? Path.Combine(Path.GetDirectoryName(probe)!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg") : "ffmpeg";
        try
        {
            foreach (var (mime, type, duration) in new[] { ("audio/mp4", "AUDIO", 1), ("video/mp4", "VIDEO", 1), ("audio/mp4", "AUDIO", 301) })
            {
                var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                var args = type == "AUDIO"
                    ? new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "anullsrc=r=8000:cl=mono", "-t", duration.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:a", "aac", path }
                    : new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=black:s=64x64:r=1", "-t", "1", "-c:v", "libx264", "-pix_fmt", "yuv420p", path };
                foreach (var arg in args) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await process.WaitForExitAsync(timeout.Token);
                Assert.Equal(0, process.ExitCode);
                var bytes = await File.ReadAllBytesAsync(path);
                var upload = await PostAsync<PresignedUpload>(client, "/v1/uploads/presign", new PresignUploadRequest(room.Id, mime, bytes.Length));
                await backend.Storage.UploadAsync(upload, bytes, mime);
                var completed = await PostAsync<CompletedUpload>(client, $"/v1/uploads/{upload.UploadId}/complete", new { });
                if (duration > 300)
                {
                    Assert.Equal("QUARANTINED", completed.Status);
                    Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/v1/conversations/{room.Id}/messages",
                        new SendMessageRequest(Guid.NewGuid(), type, null, [upload.UploadId]))).StatusCode);
                    continue;
                }
                Assert.Equal("READY", completed.Status);
                var sent = await PostAsync<MessageView>(client, $"/v1/conversations/{room.Id}/messages",
                    new SendMessageRequest(Guid.NewGuid(), type, null, [upload.UploadId]));
                var metadata = Assert.Single(sent.Attachments);
                Assert.InRange(metadata.DurationMilliseconds!.Value, 1, 3000);
                if (type == "VIDEO") Assert.Equal(64, metadata.Width);
                Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/v1/attachments/{upload.UploadId}/download")).StatusCode);
                Assert.NotNull(await staff.GetFromJsonAsync<AttachmentDownload>($"/v1/attachments/{upload.UploadId}/download"));
            }
            var invalid = await PostAsync<PresignedUpload>(client, "/v1/uploads/presign", new PresignUploadRequest(room.Id, "video/mp4", 13));
            await backend.Storage.UploadAsync(invalid, "invalid-data"u8.ToArray(), "video/mp4");
            // Declared length mismatch is rejected before probing.
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/v1/uploads/{invalid.UploadId}/complete", null)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/v1/uploads/presign",
                new PresignUploadRequest(room.Id, "application/pdf", 11 * 1024 * 1024))).StatusCode);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task RealSignalRPeersRaceToAcceptRelayOnlyToSelectedPeerAndRecoverThroughRest()
    {
        var room = await SeedAsync();
        using var client = room.Client;
        using var staff = room.Staff;
        using var other = room.Other;
        using var foreign = room.Foreign;
        await using var clientHub = Hub(client);
        await using var staffHub = Hub(staff);
        await using var otherHub = Hub(other);
        await using var foreignHub = Hub(foreign);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = timeout.Token;
        var incoming = Channel.CreateUnbounded<EventEnvelope<CallIncomingEvent>>();
        using var incomingHandler = staffHub.On<EventEnvelope<CallIncomingEvent>>("CallIncoming", e => incoming.Writer.TryWrite(e));
        await Task.WhenAll(clientHub.StartAsync(ct), staffHub.StartAsync(ct), otherHub.StartAsync(ct), foreignHub.StartAsync(ct));
        var request = new CreateCallRequest(room.Id, "VIDEO", Guid.NewGuid());
        var call = await PostAsync<CallView>(client, "/v1/calls", request);
        Assert.Equal(call.Id, (await PostAsync<CallView>(client, "/v1/calls", request)).Id);
        var notification = await incoming.Reader.ReadAsync(ct);
        Assert.Equal(call.Id, notification.Payload.CallId);
        Assert.Equal(1, notification.EventVersion);
        Assert.Equal(room.Id, notification.ConversationId);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/v1/calls/{call.Id}", ct)).StatusCode);
        await Assert.ThrowsAsync<HubException>(() => foreignHub.InvokeAsync("AcceptCall", call.Id.ToString(), ct));
        await Assert.ThrowsAsync<HubException>(() => clientHub.InvokeAsync("SendOffer", call.Id.ToString(), "v=0\r\n", ct));
        var accepts = await Task.WhenAll(Accept(staffHub), Accept(otherHub));
        Assert.Single(accepts, success => success);
        var chosenHub = accepts[0] ? staffHub : otherHub;
        var loserHub = accepts[0] ? otherHub : staffHub;
        var chosen = accepts[0] ? staff : other;
        var loser = accepts[0] ? other : staff;
        var selected = await chosen.GetFromJsonAsync<CallView>($"/v1/calls/{call.Id}", ct);
        Assert.Equal("CONNECTING", selected!.State);
        Assert.True(selected.CanSignal);
        Assert.Equal(HttpStatusCode.NotFound, (await loser.PostAsync($"/v1/calls/{call.Id}/ice-config", null, ct)).StatusCode);
        await Assert.ThrowsAsync<HubException>(() => loserHub.InvokeAsync("HangupCall", call.Id.ToString(), ct));
        var ice = await PostAsync<IceConfiguration>(chosen, $"/v1/calls/{call.Id}/ice-config", new { });
        Assert.NotEmpty(ice.IceServers);
        Assert.InRange(ice.ExpiresAt, DateTimeOffset.UtcNow, selected.ExpiresAt);
        var offers = Channel.CreateUnbounded<EventEnvelope<CallSdpEvent>>();
        using var offerHandler = chosenHub.On<EventEnvelope<CallSdpEvent>>("CallOffer", e => offers.Writer.TryWrite(e));
        var leaked = 0;
        using var leakHandler = loserHub.On<EventEnvelope<JsonElement>>("CallOffer", _ => Interlocked.Increment(ref leaked));
        var sdp = "v=0\r\ns=b06-test\r\n";
        await Assert.ThrowsAsync<HubException>(() => chosenHub.InvokeAsync("SendOffer", call.Id.ToString(), sdp, ct));
        await clientHub.InvokeAsync("SendOffer", call.Id.ToString(), sdp, ct);
        Assert.Equal(sdp, (await offers.Reader.ReadAsync(ct)).Payload.Sdp);
        await chosenHub.InvokeAsync("SendAnswer", call.Id.ToString(), sdp, ct);
        Assert.Equal("ACTIVE", (await client.GetFromJsonAsync<CallView>($"/v1/calls/{call.Id}", ct))!.State);
        var candidates = Channel.CreateUnbounded<EventEnvelope<CallIceEvent>>();
        using var candidateHandler = chosenHub.On<EventEnvelope<CallIceEvent>>("CallIceCandidate", e => candidates.Writer.TryWrite(e));
        await clientHub.InvokeAsync("SendIceCandidate", call.Id.ToString(),
            new IceCandidate("candidate:1 1 UDP 1 192.0.2.1 5000 typ host", "0", 0), ct);
        Assert.Equal(call.Id, (await candidates.Reader.ReadAsync(ct)).Payload.CallId);
        await Assert.ThrowsAsync<HubException>(() => clientHub.InvokeAsync("SendIceCandidate", call.Id.ToString(),
            new IceCandidate("bad", null, null), ct));
        await Assert.ThrowsAsync<HubException>(() => clientHub.InvokeAsync("SendAnswer", call.Id.ToString(), sdp, ct));
        await chosenHub.StopAsync(ct);
        await chosenHub.StartAsync(ct);
        Assert.True((await chosen.GetFromJsonAsync<CallView>($"/v1/calls/{call.Id}", ct))!.CanSignal);
        await clientHub.InvokeAsync("SendOffer", call.Id.ToString(), sdp, ct);
        await chosenHub.InvokeAsync("SendAnswer", call.Id.ToString(), sdp, ct);
        await chosenHub.InvokeAsync("HangupCall", call.Id.ToString(), ct);
        Assert.Equal("ENDED", (await PostAsync<CallView>(client, $"/v1/calls/{call.Id}/end", new { })).State);
        await Assert.ThrowsAsync<HubException>(() => clientHub.InvokeAsync("SendOffer", call.Id.ToString(), sdp, ct));
        Assert.Equal(0, leaked);

        async Task<bool> Accept(HubConnection hub)
        {
            try { await hub.InvokeAsync("AcceptCall", call.Id.ToString(), ct); return true; }
            catch (HubException) { return false; }
        }
    }

    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task TtlRejectionSuspensionAndSessionRevocationTerminateCalls()
    {
        var room = await SeedAsync();
        using var client = room.Client;
        using var staff = room.Staff;
        using var other = room.Other;
        using var foreign = room.Foreign;
        await using var hub = Hub(staff);
        await hub.StartAsync();
        var first = await PostAsync<CallView>(client, "/v1/calls", new CreateCallRequest(room.Id, "AUDIO"));
        await hub.InvokeAsync("RejectCall", first.Id.ToString(), "busy");
        Assert.Equal("REJECTED", (await client.GetFromJsonAsync<CallView>($"/v1/calls/{first.Id}"))!.State);
        var missed = await PostAsync<CallView>(client, "/v1/calls", new CreateCallRequest(room.Id, "AUDIO"));
        await using (var scope = backend.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            await db.Calls.Where(row => row.Id == missed.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        }
        Assert.Equal("MISSED", (await client.GetFromJsonAsync<CallView>($"/v1/calls/{missed.Id}"))!.State);
        var suspended = await PostAsync<CallView>(client, "/v1/calls", new CreateCallRequest(room.Id, "VIDEO"));
        await hub.InvokeAsync("AcceptCall", suspended.Id.ToString());
        await using (var scope = backend.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            await db.Businesses.Where(row => row.Id == room.BusinessId)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.IsActive, false));
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/v1/calls/{suspended.Id}")).StatusCode);
        await backend.Host.Services.GetServices<IHostedService>().OfType<CallWorker>().Single().SweepAsync(default);
        await using (var scope = backend.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            Assert.Equal(CallState.ENDED, (await db.Calls.SingleAsync(row => row.Id == suspended.Id)).State);
            await db.Businesses.Where(row => row.Id == room.BusinessId)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.IsActive, true));
        }
        var revoked = await PostAsync<CallView>(client, "/v1/calls", new CreateCallRequest(room.Id, "AUDIO"));
        await client.PostAsync("/v1/auth/logout", null);
        await backend.Host.Services.GetServices<IHostedService>().OfType<CallWorker>().Single().SweepAsync(default);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/v1/calls/{revoked.Id}")).StatusCode);
    }

    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task BusinessInitiatorUsesClientPeerAndEnforcesPayloadRateAndConnectingTimeout()
    {
        var room = await SeedAsync();
        using var client = room.Client;
        using var staff = room.Staff;
        using var other = room.Other;
        using var foreign = room.Foreign;
        await using var caller = Hub(staff);
        await using var callee = Hub(client);
        await caller.StartAsync();
        await callee.StartAsync();
        var call = await PostAsync<CallView>(staff, "/v1/calls", new CreateCallRequest(room.Id, "VIDEO"));
        await using (var scope = backend.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            var invalid = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"Calls\" SET \"OperatorUserId\" = NULL WHERE \"Id\" = {call.Id}"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, invalid.SqlState);
        }
        await callee.InvokeAsync("AcceptCall", call.Id.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/v1/calls/{call.Id}")).StatusCode);
        await Assert.ThrowsAsync<HubException>(() => caller.InvokeAsync("SendOffer", call.Id.ToString(), "v=0\r\n" + new string('x', 24576)));
        await Assert.ThrowsAsync<HubException>(() => callee.InvokeAsync("SendOffer", call.Id.ToString(), "v=0\r\n"));
        await caller.InvokeAsync("SendOffer", call.Id.ToString(), "v=0\r\ns=business\r\n");
        await using (var scope = backend.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            await db.Calls.Where(row => row.Id == call.Id).ExecuteUpdateAsync(update =>
                update.SetProperty(row => row.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        }
        Assert.Equal("ENDED", (await client.GetFromJsonAsync<CallView>($"/v1/calls/{call.Id}"))!.State);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/v1/calls/{call.Id}/ice-config", null)).StatusCode);
        HubException? last = null;
        for (var i = 0; i < 61; i++)
            last = await Assert.ThrowsAsync<HubException>(() => callee.InvokeAsync("AcceptCall", call.Id.ToString()));
        Assert.Contains("rate limit", last!.Message, StringComparison.OrdinalIgnoreCase);
    }

    private HubConnection Hub(HttpClient actor) => new HubConnectionBuilder().WithUrl("https://localhost/hubs/messaging", options =>
    {
        options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
        options.SkipNegotiation = true;
        options.AccessTokenProvider = () => Task.FromResult(actor.DefaultRequestHeaders.Authorization?.Parameter);
        options.WebSocketFactory = async (context, ct) =>
        {
            var socket = backend.Host.Server.CreateWebSocketClient();
            socket.ConfigureRequest = request => request.Headers.Authorization = actor.DefaultRequestHeaders.Authorization?.ToString();
            return await socket.ConnectAsync(context.Uri, ct);
        };
    }).Build();

    private async Task<Room> SeedAsync(BackendFixture? fixture = null)
    {
        var host = (fixture ?? backend).Host;
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        async Task<(User User, HttpClient Http)> Actor(UserKind kind)
        {
            var email = $"{Guid.NewGuid():N}@example.test";
            var user = new User
            {
                Kind = kind,
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                DisplayName = "B06 fixture",
                EmailVerifiedAt = DateTimeOffset.UtcNow
            };
            user.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>().HashPassword(user, Password);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            var http = fixture?.NetworkCertificate is { } certificate
                ? new HttpClient(new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, peer, _, _) => peer?.Thumbprint == certificate.Thumbprint
                })
                { BaseAddress = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) }
                : host.CreateClient();
            var issued = await PostAsync<AuthTokens>(http, "/v1/auth/login", new LoginRequest(email, Password));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.AccessToken);
            return (user, http);
        }
        var client = await Actor(UserKind.CLIENT);
        var staff = await Actor(UserKind.BUSINESS_MEMBER);
        var other = await Actor(UserKind.BUSINESS_MEMBER);
        var foreign = await Actor(UserKind.CLIENT);
        var business = new Business
        {
            LegalName = "B06",
            DisplayName = "B06",
            RegistrationNumber = Guid.NewGuid().ToString("N"),
            Country = "KZ",
            Category = "Test",
            Status = BusinessStatus.VERIFIED,
            IsActive = true
        };
        db.Businesses.Add(business);
        foreach (var user in new[] { staff.User, other.User })
            db.BusinessMemberships.Add(new BusinessMembership { UserId = user.Id, BusinessId = business.Id, Role = BusinessRole.AGENT });
        var room = new Conversation
        {
            ClientUserId = client.User.Id,
            BusinessId = business.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Conversations.Add(room);
        await db.SaveChangesAsync();
        return new Room(room.Id, business.Id, client.Http, staff.Http, other.Http, foreign.Http);
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        Assert.True(response.IsSuccessStatusCode, $"{path} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private sealed record Room(Guid Id, Guid BusinessId, HttpClient Client, HttpClient Staff, HttpClient Other, HttpClient Foreign);
}

