using Microsoft.EntityFrameworkCore;

namespace chat.Infrastructure.Persistence;

public sealed class ChatDbContext(DbContextOptions<ChatDbContext> options) : DbContext(options);
