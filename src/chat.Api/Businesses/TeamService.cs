using System.Security.Claims;
using chat.Api.Identity;
using chat.Api.Messaging;
using chat.Api.Middleware;
using chat.Contracts.Businesses;
using chat.Domain.Businesses;
using chat.Domain.Identity;
using chat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace chat.Api.Businesses;

internal sealed class TeamService(ChatDbContext db, AccountCredentials credentials, TimeProvider clock)
{
    public const string AssignmentChanged = "ConversationAssignmentChanged";

    private async Task<Guid> BusinessAsync(Guid actor, Guid? requested, bool manage, CancellationToken ct)
    {
        var ids = await ConversationAccess.EligibleMemberships(db).Where(row => row.UserId == actor
                && (!manage || row.Role == BusinessRole.OWNER || row.Role == BusinessRole.ADMIN)
                && (requested == null || row.BusinessId == requested)
                && db.Businesses.Any(b => b.Id == row.BusinessId && b.IsActive && b.Status == BusinessStatus.VERIFIED))
            .Select(row => row.BusinessId).Take(2).ToArrayAsync(ct);
        if (ids.Length == 0) throw new ApiFailure(404);
        if (ids.Length != 1) throw new ApiFailure(400);
        return ids[0];
    }

    private async Task<BusinessMembership> LockManagerAsync(Guid actor, Guid id, CancellationToken ct)
    {
        await LockBusinessAsync(id, ct);
        var member = await db.BusinessMemberships.SingleOrDefaultAsync(row => row.BusinessId == id && row.UserId == actor, ct);
        return member is { IsActive: true, Role: BusinessRole.OWNER or BusinessRole.ADMIN } ? member : throw new ApiFailure(404);
    }

    private async Task LockBusinessAsync(Guid id, CancellationToken ct)
    {
        var rows = await db.Businesses.FromSql($"SELECT * FROM \"Businesses\" WHERE \"Id\" = {id} FOR UPDATE").ToListAsync(ct);
        if (rows.SingleOrDefault() is not { IsActive: true, Status: BusinessStatus.VERIFIED }) throw new ApiFailure(404);
    }

    public async Task<TeamMemberView[]> MembersAsync(Guid actor, Guid? businessId, CancellationToken ct)
    {
        var id = await BusinessAsync(actor, businessId, true, ct);
        return await (from member in db.BusinessMemberships.AsNoTracking()
                      join user in db.Users on member.UserId equals user.Id
                      where member.BusinessId == id
                      orderby user.DisplayName, user.Id
                      select new TeamMemberView(user.Id, user.DisplayName, member.Role.ToString(), member.IsActive)).ToArrayAsync(ct);
    }

    public async Task<InvitationView[]> InvitationsAsync(Guid actor, Guid? businessId, CancellationToken ct)
    {
        var id = await BusinessAsync(actor, businessId, true, ct);
        var rows = await db.TeamInvitations.AsNoTracking().Where(row => row.BusinessId == id)
            .OrderByDescending(row => row.CreatedAt).Take(100).ToArrayAsync(ct);
        return rows.Select(View).ToArray();
    }

    public async Task<InvitationCreated> InviteAsync(Guid actor, Guid? businessId, InviteTeamRequest request, CancellationToken ct)
    {
        var id = await BusinessAsync(actor, businessId, true, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var manager = await LockManagerAsync(actor, id, ct);
        var role = Enum.Parse<BusinessRole>(request.Role);
        if (manager.Role != BusinessRole.OWNER && role != BusinessRole.AGENT) throw new ApiFailure(403);
        var now = clock.GetUtcNow();
        if (await db.TeamInvitations.CountAsync(row => row.BusinessId == id && row.CreatedAt > now.AddHours(-1), ct) >= 100)
            throw new ApiFailure(429);
        var token = AccountCredentials.NewToken();
        var row = new TeamInvitation
        {
            BusinessId = id,
            CreatedBy = actor,
            Email = AccountCredentials.NormalizeEmail(request.Email),
            Role = role,
            TokenHash = AccountCredentials.TokenHash(token),
            CreatedAt = now,
            ExpiresAt = now.AddHours(48)
        };
        db.TeamInvitations.Add(row);
        Audit(actor, row.Id, "TEAM_INVITE");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new InvitationCreated(View(row), token);
    }

    public async Task RevokeAsync(Guid actor, Guid? businessId, Guid invitationId, CancellationToken ct)
    {
        var id = await BusinessAsync(actor, businessId, true, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var manager = await LockManagerAsync(actor, id, ct);
        var row = await db.TeamInvitations.SingleOrDefaultAsync(row => row.Id == invitationId && row.BusinessId == id, ct)
            ?? throw new ApiFailure(404);
        if (manager.Role != BusinessRole.OWNER && row.Role != BusinessRole.AGENT) throw new ApiFailure(403);
        if (row.AcceptedAt != null) throw new ApiFailure(409);
        row.RevokedAt ??= clock.GetUtcNow();
        Audit(actor, row.Id, "TEAM_INVITE_REVOKE");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<TeamMemberView> AcceptAsync(ClaimsPrincipal principal, AcceptInvitationRequest request, CancellationToken ct)
    {
        var hash = AccountCredentials.TokenHash(request.Token);
        var snapshot = await db.TeamInvitations.AsNoTracking().SingleOrDefaultAsync(row => row.TokenHash == hash, ct)
            ?? throw new ApiFailure(404);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockBusinessAsync(snapshot.BusinessId, ct);
        var invitation = await db.TeamInvitations.SingleAsync(row => row.Id == snapshot.Id, ct);
        if (invitation.RevokedAt != null || invitation.AcceptedAt != null || invitation.ExpiresAt <= clock.GetUtcNow())
            throw new ApiFailure(409);
        // A revoked/demoted inviter cannot leave privileged invitations behind.
        var inviter = await db.BusinessMemberships.SingleOrDefaultAsync(row => row.BusinessId == invitation.BusinessId
            && row.UserId == invitation.CreatedBy && db.Users.Any(user => user.Id == row.UserId
                && user.IsActive && user.Kind == UserKind.BUSINESS_MEMBER), ct);
        if (inviter is not { IsActive: true } || (inviter.Role != BusinessRole.OWNER
            && (inviter.Role != BusinessRole.ADMIN || invitation.Role != BusinessRole.AGENT))) throw new ApiFailure(409);
        var user = await db.Users.SingleOrDefaultAsync(row => row.NormalizedEmail == invitation.Email, ct);
        if (user != null)
        {
            if (principal.Identity?.IsAuthenticated != true || principal.UserId() != user.Id
                || user.Kind != UserKind.BUSINESS_MEMBER || !user.IsActive) throw new ApiFailure(403);
        }
        else
        {
            if (principal.Identity?.IsAuthenticated == true || string.IsNullOrWhiteSpace(request.Password)
                || string.IsNullOrWhiteSpace(request.DisplayName)) throw new ApiFailure(400);
            user = credentials.CreateUser(invitation.Email, request.Password, request.DisplayName, UserKind.BUSINESS_MEMBER);
            var verification = credentials.PrepareVerification(user);
            if (verification != null) await credentials.CheckMailCapacityAsync(ct);
            db.Users.Add(user);
            credentials.QueueVerification(user, verification);
        }
        var membership = await db.BusinessMemberships.SingleOrDefaultAsync(row => row.BusinessId == invitation.BusinessId
            && row.UserId == user.Id, ct);
        if (membership != null) throw new ApiFailure(409); // Existing roles are changed only by the explicit member endpoint.
        membership = new BusinessMembership { UserId = user.Id, BusinessId = invitation.BusinessId, Role = invitation.Role };
        db.BusinessMemberships.Add(membership);
        invitation.AcceptedAt = clock.GetUtcNow();
        Audit(user.Id, invitation.Id, "TEAM_INVITE_ACCEPT");
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Invitations for the same new address in different businesses may race on the global email key.
            throw new ApiFailure(409);
        }
        return new TeamMemberView(user.Id, user.DisplayName, membership.Role.ToString(), true);
    }

    public async Task<TeamMemberView> UpdateAsync(Guid actor, Guid? businessId, Guid target, UpdateMemberRequest request, CancellationToken ct)
    {
        var id = await BusinessAsync(actor, businessId, true, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var manager = await LockManagerAsync(actor, id, ct);
        var membership = await db.BusinessMemberships.SingleOrDefaultAsync(row => row.BusinessId == id && row.UserId == target, ct)
            ?? throw new ApiFailure(404);
        var role = Enum.Parse<BusinessRole>(request.Role);
        if (manager.Role != BusinessRole.OWNER && (membership.Role != BusinessRole.AGENT || role != BusinessRole.AGENT))
            throw new ApiFailure(403);
        if (membership is { IsActive: true, Role: BusinessRole.OWNER } && (!request.IsActive || role != BusinessRole.OWNER)
            && await db.BusinessMemberships.CountAsync(row => row.BusinessId == id && row.IsActive && row.Role == BusinessRole.OWNER
                && db.Users.Any(user => user.Id == row.UserId && user.IsActive && user.Kind == UserKind.BUSINESS_MEMBER), ct) <= 1)
            throw new ApiFailure(409);
        membership.Role = role;
        membership.IsActive = request.IsActive;
        if (!request.IsActive)
        {
            var assignments = await db.ConversationAssignments.Where(row => row.BusinessId == id && row.UserId == target).ToArrayAsync(ct);
            await Outbox.RequireCapacityAsync(db, assignments.Length, ct);
            foreach (var assignment in assignments) ChangeAssignment(assignment, null, actor);
        }
        Audit(actor, target, "TEAM_MEMBER_UPDATE");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        var user = await db.Users.SingleAsync(row => row.Id == target, ct);
        return new TeamMemberView(target, user.DisplayName, role.ToString(), request.IsActive);
    }

    public async Task<AssignmentView> GetAssignmentAsync(Guid actor, Guid conversationId, CancellationToken ct)
    {
        var conversation = await db.Conversations.AsNoTracking().SingleOrDefaultAsync(row => row.Id == conversationId, ct)
            ?? throw new ApiFailure(404);
        await BusinessAsync(actor, conversation.BusinessId, false, ct);
        var row = await db.ConversationAssignments.AsNoTracking().SingleOrDefaultAsync(row => row.ConversationId == conversationId, ct);
        return row == null ? new AssignmentView(conversationId, null, 0, null) : Assignment(row);
    }

    public async Task<AssignmentView> AssignAsync(Guid actor, Guid conversationId, AssignmentRequest request, CancellationToken ct)
    {
        var conversation = await db.Conversations.AsNoTracking().SingleOrDefaultAsync(row => row.Id == conversationId, ct)
            ?? throw new ApiFailure(404);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockManagerAsync(actor, conversation.BusinessId, ct);
        if (request.UserId is { } userId && !await ConversationAccess.EligibleMemberships(db).AnyAsync(row => row.BusinessId == conversation.BusinessId
            && row.UserId == userId && db.Users.Any(user => user.Id == userId && user.IsActive && user.Kind == UserKind.BUSINESS_MEMBER), ct)) throw new ApiFailure(404);
        var row = await db.ConversationAssignments.SingleOrDefaultAsync(row => row.ConversationId == conversationId, ct);
        if ((row?.Revision ?? 0) != request.ExpectedRevision) throw new ApiFailure(409);
        if (row == null)
        {
            row = new ConversationAssignment
            {
                ConversationId = conversationId,
                BusinessId = conversation.BusinessId,
                UpdatedBy = actor,
                UpdatedAt = clock.GetUtcNow()
            };
            db.ConversationAssignments.Add(row);
        }
        await Outbox.RequireCapacityAsync(db, 1, ct);
        ChangeAssignment(row, request.UserId, actor);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Assignment(row);
    }

    private void ChangeAssignment(ConversationAssignment row, Guid? userId, Guid actor)
    {
        row.UserId = userId;
        row.UpdatedBy = actor;
        row.UpdatedAt = clock.GetUtcNow();
        row.Revision++;
        Outbox.Add(db, AssignmentChanged, row.UpdatedAt, Assignment(row), row.ConversationId, row.BusinessId);
        Audit(actor, row.ConversationId, "CONVERSATION_ASSIGNMENT");
    }

    private void Audit(Guid actor, Guid entity, string action) => db.AuditLogs.Add(new AuditLog
    { ActorId = actor, EntityId = entity, Action = action, CreatedAt = clock.GetUtcNow() });
    private static AssignmentView Assignment(ConversationAssignment row) => new(row.ConversationId, row.UserId, row.Revision, row.UpdatedAt);
    private static InvitationView View(TeamInvitation row) => new(row.Id, row.BusinessId, row.Email, row.Role.ToString(),
        row.ExpiresAt, row.AcceptedAt, row.RevokedAt);

    public async Task<BusinessAnalytics> AnalyticsAsync(Guid actor, Guid? businessId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var id = await BusinessAsync(actor, businessId, true, ct);
        var end = to ?? clock.GetUtcNow();
        var start = from ?? end.AddDays(-30);
        if (start >= end || end - start > TimeSpan.FromDays(90) || start.Offset != TimeSpan.Zero || end.Offset != TimeSpan.Zero)
            throw new ApiFailure(400);
        var result = await db.Database.SqlQuery<AnalyticsRow>($"""
            WITH cohort AS (
                SELECT c."Id", first_client.at AS first_at, response.at AS response_at
                FROM "Conversations" c
                LEFT JOIN LATERAL (SELECT min(m."CreatedAt") AS at FROM "Messages" m
                    WHERE m."ConversationId" = c."Id" AND m."AuthorSide" = 'CLIENT' AND m."CreatedAt" < {end}) first_client ON true
                LEFT JOIN LATERAL (SELECT min(m."CreatedAt") AS at FROM "Messages" m
                    WHERE m."ConversationId" = c."Id" AND m."AuthorSide" = 'BUSINESS'
                    AND m."CreatedAt" >= first_client.at AND m."CreatedAt" < {end}) response ON true
                WHERE c."BusinessId" = {id} AND c."CreatedAt" >= {start} AND c."CreatedAt" < {end}
            )
            SELECT count(*) AS "Conversations", count(response_at) AS "Responded",
                count(*) FILTER (WHERE first_at IS NOT NULL AND response_at IS NULL) AS "Unanswered",
                avg(extract(epoch FROM (response_at - first_at)) * 1000)::double precision AS "Average",
                (SELECT count(*) FROM "Messages" m JOIN "Conversations" c ON c."Id" = m."ConversationId"
                 WHERE c."BusinessId" = {id} AND m."CreatedAt" >= {start} AND m."CreatedAt" < {end}
                 AND m."AuthorSide" = 'CLIENT') AS "ClientMessages",
                (SELECT count(*) FROM "Messages" m JOIN "Conversations" c ON c."Id" = m."ConversationId"
                 WHERE c."BusinessId" = {id} AND m."CreatedAt" >= {start} AND m."CreatedAt" < {end}
                 AND m."AuthorSide" = 'BUSINESS') AS "BusinessMessages"
            FROM cohort
            """).SingleAsync(ct);
        return new BusinessAnalytics(start, end, result.Conversations, result.ClientMessages, result.BusinessMessages,
            result.Responded, result.Unanswered, result.Average);
    }

    private sealed class AnalyticsRow
    {
        public long Conversations { get; set; }
        public long Responded { get; set; }
        public long Unanswered { get; set; }
        public long ClientMessages { get; set; }
        public long BusinessMessages { get; set; }
        public double? Average { get; set; }
    }
}
