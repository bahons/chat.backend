using System.Net;
using chat.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace chat.IntegrationTests;

public sealed class PostgresTests
{
    [Fact]
    [Trait("Dependency", "Docker")]
    public async Task AppliesMigrationsAndReportsReady()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await postgres.StartAsync(timeout.Token);

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Postgres", postgres.GetConnectionString()));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            Assert.False(db.Database.HasPendingModelChanges());
            await db.Database.MigrateAsync(timeout.Token);
            Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync(timeout.Token));
            Assert.Empty(await db.Database.GetPendingMigrationsAsync(timeout.Token));
        }

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/ready", timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
