using chat.Domain.Businesses;
using chat.Domain.Identity;
using chat.Domain.Messaging;
using Microsoft.EntityFrameworkCore;

namespace chat.Infrastructure.Persistence;

public sealed class ChatDbContext(DbContextOptions<ChatDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshSession> RefreshSessions => Set<RefreshSession>();
    public DbSet<AccountMail> AccountMails => Set<AccountMail>();
    public DbSet<BusinessMembership> BusinessMemberships => Set<BusinessMembership>();

    public DbSet<Business> Businesses => Set<Business>();
    public DbSet<BusinessApplication> BusinessApplications => Set<BusinessApplication>();
    public DbSet<ApplicationDraft> ApplicationDrafts => Set<ApplicationDraft>();
    public DbSet<VerificationDocument> VerificationDocuments => Set<VerificationDocument>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageReadReceipt> MessageReadReceipts => Set<MessageReadReceipt>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<DeviceRegistration> DeviceRegistrations => Set<DeviceRegistration>();
    public DbSet<PushDelivery> PushDeliveries => Set<PushDelivery>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("pg_trgm");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ChatDbContext).Assembly);
    }
}
