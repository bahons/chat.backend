using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using chat.Contracts.Businesses;
using chat.Contracts.Identity;
using chat.Contracts.Messaging;
using chat.Domain.Businesses;
using chat.Domain.Identity;
using chat.Domain.Messaging;
using chat.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace chat.IntegrationTests;

public sealed class TeamTests(BackendFixture backend) : IClassFixture<BackendFixture>
{
    private const string Password = "b07-integration-password";

    [Fact]
    public async Task TeamInvitationsAssignmentsAnalyticsAndLiveRevocation()
    {
        await using var scope = backend.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        async Task<(User User, HttpClient Http)> Actor(UserKind kind)
        {
            var email = $"{Guid.NewGuid():N}@example.test";
            var user = new User
            {
                Kind = kind,
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                DisplayName = "B07",
                EmailVerifiedAt = DateTimeOffset.UtcNow
            };
            user.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>().HashPassword(user, Password);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            var http = backend.Host.CreateClient();
            var tokens = await Post<AuthTokens>(http, "/v1/auth/login", new LoginRequest(email, Password));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            return (user, http);
        }
        var owner = await Actor(UserKind.BUSINESS_MEMBER);
        var agent = await Actor(UserKind.BUSINESS_MEMBER);
        var admin = await Actor(UserKind.BUSINESS_MEMBER);
        var foreign = await Actor(UserKind.BUSINESS_MEMBER);
        var client = await Actor(UserKind.CLIENT);
        using var ownerHttp = owner.Http;
        using var agentHttp = agent.Http;
        using var adminHttp = admin.Http;
        using var foreignHttp = foreign.Http;
        using var clientHttp = client.Http;
        var business = new Business
        {
            LegalName = "B07",
            DisplayName = "B07",
            Country = "KZ",
            Category = "Test",
            RegistrationNumber = Guid.NewGuid().ToString("N"),
            Status = BusinessStatus.VERIFIED,
            IsActive = true
        };
        db.Businesses.Add(business);
        db.BusinessMemberships.AddRange(new BusinessMembership { BusinessId = business.Id, UserId = owner.User.Id, Role = BusinessRole.OWNER },
            new BusinessMembership { BusinessId = business.Id, UserId = admin.User.Id, Role = BusinessRole.ADMIN },
            new BusinessMembership { BusinessId = business.Id, UserId = agent.User.Id, Role = BusinessRole.AGENT });
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        var conversation = new Conversation { BusinessId = business.Id, ClientUserId = client.User.Id, CreatedAt = start, UpdatedAt = start };
        db.Conversations.Add(conversation);
        db.Messages.AddRange(new Message
        {
            ConversationId = conversation.Id,
            AuthorUserId = client.User.Id,
            AuthorSide = ParticipantSide.CLIENT,
            Type = MessageType.TEXT,
            Text = "client",
            ClientMessageId = Guid.NewGuid(),
            CreatedAt = start.AddMinutes(1)
        },
            new Message
            {
                ConversationId = conversation.Id,
                AuthorUserId = agent.User.Id,
                AuthorSide = ParticipantSide.BUSINESS,
                Type = MessageType.TEXT,
                Text = "reply",
                ClientMessageId = Guid.NewGuid(),
                CreatedAt = start.AddMinutes(2)
            });
        await db.SaveChangesAsync();
        var team = (await ownerHttp.GetFromJsonAsync<TeamMemberView[]>("/v1/team"))!;
        Assert.Equal(3, team.Length);
        Assert.Equal(HttpStatusCode.NotFound, (await agentHttp.GetAsync("/v1/team")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreignHttp.GetAsync($"/v1/team?businessId={business.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clientHttp.GetAsync("/v1/team")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ownerHttp.PatchAsJsonAsync($"/v1/my-business/members/{owner.User.Id}",
            new UpdateMemberRequest("AGENT", true))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await adminHttp.PatchAsJsonAsync($"/v1/my-business/members/{owner.User.Id}",
            new UpdateMemberRequest("AGENT", true))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await adminHttp.PostAsJsonAsync("/v1/my-business/invitations",
            new InviteTeamRequest(foreign.User.Email, "OWNER"))).StatusCode);
        var otherBusiness = new Business
        {
            LegalName = "Foreign",
            DisplayName = "Foreign",
            Country = "KZ",
            Category = "Test",
            RegistrationNumber = Guid.NewGuid().ToString("N"),
            Status = BusinessStatus.VERIFIED,
            IsActive = true
        };
        db.Businesses.Add(otherBusiness);
        db.BusinessMemberships.Add(new BusinessMembership { BusinessId = otherBusiness.Id, UserId = foreign.User.Id, Role = BusinessRole.OWNER });
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await foreignHttp.PatchAsJsonAsync($"/v1/conversations/{conversation.Id}/assignment",
            new AssignmentRequest(foreign.User.Id, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerHttp.PatchAsJsonAsync($"/v1/conversations/{conversation.Id}/assignment",
            new AssignmentRequest(foreign.User.Id, 0))).StatusCode);

        var invitation = await Post<InvitationCreated>(ownerHttp, "/v1/my-business/invitations", new InviteTeamRequest(foreign.User.Email, "AGENT"));
        using var anonymous = backend.Host.CreateClient();
        var disabledInviter = await Post<InvitationCreated>(adminHttp, "/v1/my-business/invitations", new InviteTeamRequest("disabled-inviter@example.test", "AGENT"));
        await db.Users.Where(row => row.Id == admin.User.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.IsActive, false));
        Assert.Equal(HttpStatusCode.Conflict, (await anonymous.PostAsJsonAsync("/v1/invitations/accept",
            new AcceptInvitationRequest(disabledInviter.Token, Password, "Disabled inviter"))).StatusCode);
        await db.Users.Where(row => row.Id == admin.User.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.IsActive, true));
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.PostAsJsonAsync("/v1/invitations/accept", new AcceptInvitationRequest(invitation.Token))).StatusCode);
        var accepts = await Task.WhenAll(foreignHttp.PostAsJsonAsync("/v1/invitations/accept", new AcceptInvitationRequest(invitation.Token)),
            foreignHttp.PostAsJsonAsync("/v1/invitations/accept", new AcceptInvitationRequest(invitation.Token)));
        Assert.Single(accepts, row => row.StatusCode == HttpStatusCode.OK);
        Assert.Single(accepts, row => row.StatusCode == HttpStatusCode.Conflict);
        foreach (var response in accepts) response.Dispose();
        var revoked = await Post<InvitationCreated>(ownerHttp, "/v1/my-business/invitations", new InviteTeamRequest("new@example.test", "AGENT"));
        Assert.Equal(HttpStatusCode.NoContent, (await ownerHttp.DeleteAsync($"/v1/my-business/invitations/{revoked.Invitation.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await anonymous.PostAsJsonAsync("/v1/invitations/accept",
            new AcceptInvitationRequest(revoked.Token, Password, "New"))).StatusCode);
        var expired = await Post<InvitationCreated>(ownerHttp, "/v1/my-business/invitations", new InviteTeamRequest("expired@example.test", "AGENT"));
        await db.TeamInvitations.Where(row => row.Id == expired.Invitation.Id).ExecuteUpdateAsync(update => update
            .SetProperty(row => row.CreatedAt, DateTimeOffset.UtcNow.AddDays(-3)).SetProperty(row => row.ExpiresAt, DateTimeOffset.UtcNow.AddDays(-1)));
        Assert.Equal(HttpStatusCode.Conflict, (await anonymous.PostAsJsonAsync("/v1/invitations/accept",
            new AcceptInvitationRequest(expired.Token, Password, "Expired"))).StatusCode);
        var newEmail = $"{Guid.NewGuid():N}@example.test";
        var fresh = await Post<InvitationCreated>(ownerHttp, "/v1/my-business/invitations", new InviteTeamRequest(newEmail, "AGENT"));
        var newMember = await Post<TeamMemberView>(anonymous, "/v1/invitations/accept", new AcceptInvitationRequest(fresh.Token, Password, "New member"));
        var newUser = await db.Users.AsNoTracking().SingleAsync(row => row.Id == newMember.UserId);
        Assert.Equal(UserKind.BUSINESS_MEMBER, newUser.Kind);
        Assert.Null(newUser.EmailVerifiedAt);
        Assert.NotNull(newUser.VerificationHash);
        Assert.True(await db.AccountMails.AnyAsync(row => row.UserId == newUser.Id));

        var path = $"/v1/conversations/{conversation.Id}/assignment";
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerHttp.PatchAsJsonAsync(path, new { userId = agent.User.Id })).StatusCode);
        Assert.Equal(0, (await agentHttp.GetFromJsonAsync<AssignmentView>(path))!.Revision);
        Assert.Equal(HttpStatusCode.NotFound, (await agentHttp.PatchAsJsonAsync(path, new AssignmentRequest(agent.User.Id, 0))).StatusCode);
        await using var hub = Hub(agentHttp);
        await using var clientHub = Hub(clientHttp);
        var changed = new TaskCompletionSource<EventEnvelope<JsonElement>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<EventEnvelope<JsonElement>>("ConversationAssignmentChanged", value => changed.TrySetResult(value));
        clientHub.On<EventEnvelope<JsonElement>>("ConversationAssignmentChanged", _ => leaked.TrySetResult());
        await hub.StartAsync();
        await clientHub.StartAsync();
        await hub.InvokeAsync("JoinConversation", conversation.Id.ToString());
        await clientHub.InvokeAsync("JoinConversation", conversation.Id.ToString());
        var assignments = await Task.WhenAll(ownerHttp.PatchAsJsonAsync(path, new AssignmentRequest(agent.User.Id, 0)),
            adminHttp.PatchAsJsonAsync(path, new AssignmentRequest(foreign.User.Id, 0)));
        Assert.Single(assignments, row => row.StatusCode == HttpStatusCode.OK);
        Assert.Single(assignments, row => row.StatusCode == HttpStatusCode.Conflict);
        foreach (var response in assignments) response.Dispose();
        var evt = await changed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, evt.Payload.GetProperty("revision").GetInt64());
        Assert.False(leaked.Task.IsCompleted);
        var analytics = (await ownerHttp.GetFromJsonAsync<BusinessAnalytics>("/v1/my-business/analytics"))!;
        Assert.Equal(1, analytics.Conversations);
        Assert.Equal(1, analytics.ClientMessages);
        Assert.Equal(1, analytics.BusinessMessages);
        Assert.Equal(1, analytics.RespondedConversations);
        Assert.Equal(60000d, analytics.AverageFirstResponseMilliseconds);
        Assert.Equal(HttpStatusCode.NotFound, (await agentHttp.GetAsync("/v1/my-business/analytics")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerHttp.GetAsync("/v1/my-business/analytics?from=2020-01-01T00:00:00Z")).StatusCode);

        // Revocation clears the assignee whichever concurrent assignment won, preserving the company history.
        var assigned = (await ownerHttp.GetFromJsonAsync<AssignmentView>(path))!;
        var target = assigned.UserId!.Value;
        var disconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Closed += _ => { disconnect.TrySetResult(); return Task.CompletedTask; };
        await ownerHttp.PatchAsJsonAsync($"/v1/my-business/members/{target}", new UpdateMemberRequest("AGENT", false));
        Assert.Null((await ownerHttp.GetFromJsonAsync<AssignmentView>(path))!.UserId);
        if (target != agent.User.Id)
            await ownerHttp.PatchAsJsonAsync($"/v1/my-business/members/{agent.User.Id}", new UpdateMemberRequest("AGENT", false));
        Assert.Equal(HttpStatusCode.NotFound, (await agentHttp.GetAsync($"/v1/conversations/{conversation.Id}")).StatusCode);
        await disconnect.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotNull(await ownerHttp.GetFromJsonAsync<ConversationView>($"/v1/conversations/{conversation.Id}"));
        Assert.False(leaked.Task.IsCompleted);
        Assert.DoesNotContain(invitation.Token, (await ownerHttp.GetStringAsync("/v1/my-business/invitations")));
        await ownerHttp.PatchAsJsonAsync($"/v1/my-business/members/{admin.User.Id}", new UpdateMemberRequest("OWNER", true));
        var demotions = await Task.WhenAll(ownerHttp.PatchAsJsonAsync($"/v1/my-business/members/{owner.User.Id}", new UpdateMemberRequest("AGENT", true)),
            adminHttp.PatchAsJsonAsync($"/v1/my-business/members/{admin.User.Id}", new UpdateMemberRequest("AGENT", true)));
        Assert.Single(demotions, row => row.StatusCode == HttpStatusCode.OK);
        Assert.Single(demotions, row => row.StatusCode == HttpStatusCode.Conflict);
        foreach (var response in demotions) response.Dispose();
        Assert.Equal(1, await db.BusinessMemberships.CountAsync(row => row.BusinessId == business.Id && row.IsActive && row.Role == BusinessRole.OWNER));
    }

    private HubConnection Hub(HttpClient actor) => new HubConnectionBuilder().WithUrl("https://localhost/hubs/messaging", options =>
    {
        options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
        options.SkipNegotiation = true;
        options.AccessTokenProvider = () => Task.FromResult(actor.DefaultRequestHeaders.Authorization?.Parameter);
        options.WebSocketFactory = async (_, ct) =>
        {
            var socket = backend.Host.Server.CreateWebSocketClient();
            socket.ConfigureRequest = request => request.Headers.Authorization = actor.DefaultRequestHeaders.Authorization!.ToString();
            return await socket.ConnectAsync(new Uri("wss://localhost/hubs/messaging"), ct);
        };
    }).Build();

    private static async Task<T> Post<T>(HttpClient http, string path, object request)
    {
        using var response = await http.PostAsJsonAsync(path, request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
