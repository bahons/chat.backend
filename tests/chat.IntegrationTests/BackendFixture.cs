using System.Security.Cryptography;
using System.Threading.Channels;
using chat.Api.Identity;
using chat.Application.Identity;
using chat.Application.Media;
using chat.Application.Push;
using chat.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using Xunit;

namespace chat.IntegrationTests;

public sealed class BackendFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly TestEmailSender sender = new();
    private readonly string signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private readonly string mailKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public TestObjectStorage Storage { get; } = new();
    public TestPushGateway Push { get; } = new();
    public Func<AccountEmail, CancellationToken, Task>? BeforeMailSend
    {
        get => sender.BeforeSend;
        set => sender.BeforeSend = value;
    }
    public WebApplicationFactory<Program> Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await postgres.StartAsync(timeout.Token);
        Host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Postgres", postgres.GetConnectionString())
            .UseSetting("Jwt:Issuer", "integration-tests")
            .UseSetting("Jwt:Audience", "integration-tests")
            .UseSetting("Jwt:SigningKeyBase64", signingKey)
            .UseSetting("AccountEmail:QueueKeyBase64", mailKey)
            .UseSetting("Identity:AuthRequestsPerMinute", "120")
            .UseSetting("AccountEmail:Port", Environment.GetEnvironmentVariable("CHAT_TEST_SMTP_PORT") ?? "1025")
            .UseSetting("RateLimiting:PermitLimit", "1200")
            .UseSetting("Push:Enabled", "true")
            .UseSetting("ObjectStorage:ServiceUrl", Environment.GetEnvironmentVariable("CHAT_TEST_S3_URL") ?? "")
            .UseSetting("ObjectStorage:AccessKey", Environment.GetEnvironmentVariable("CHAT_TEST_S3_ACCESS_KEY") ?? "")
            .UseSetting("ObjectStorage:SecretKey", Environment.GetEnvironmentVariable("CHAT_TEST_S3_SECRET_KEY") ?? "")
            .UseSetting("ObjectStorage:CreateBucket", "true")
            .UseSetting("FileScan:Host", Environment.GetEnvironmentVariable("CHAT_TEST_CLAMAV_HOST") ?? "test-scanner")
            .UseSetting("FileScan:Port", Environment.GetEnvironmentVariable("CHAT_TEST_CLAMAV_PORT") ?? "3310")
            .ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IAccountEmailSender>(provider =>
                    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CHAT_TEST_SMTP_PORT"))
                        ? sender
                        : new RelayedEmailSender(new SmtpAccountEmailSender(provider.GetRequiredService<IOptions<AccountEmailOptions>>()), sender)));
                services.Replace(ServiceDescriptor.Singleton<IPushGateway>(Push));
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CHAT_TEST_CLAMAV_HOST")))
                {
                    services.Replace(ServiceDescriptor.Singleton<IFileScanner, TestFileScanner>());
                }
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CHAT_TEST_S3_URL")))
                {
                    services.Replace(ServiceDescriptor.Singleton<IPrivateObjectStorage>(Storage));
                }
            }));
        await using var scope = Host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ChatDbContext>().Database.MigrateAsync(timeout.Token);
    }

    public async Task<string> ReadCodeAsync(string subject)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var mail in sender.Mail.Reader.ReadAllAsync(timeout.Token))
        {
            if (mail.Subject == subject)
            {
                return mail.Body.Split(' ')[^1];
            }
        }

        throw new InvalidOperationException("Expected an account email.");
    }

    public async Task DisposeAsync()
    {
        if (Host is not null)
        {
            await Host.DisposeAsync();
        }

        await postgres.DisposeAsync();
    }

    private sealed class TestEmailSender : IAccountEmailSender
    {
        public Channel<AccountEmail> Mail { get; } = Channel.CreateUnbounded<AccountEmail>();

        public Func<AccountEmail, CancellationToken, Task>? BeforeSend { get; set; }

        public async Task SendAsync(AccountEmail message, CancellationToken cancellationToken)
        {
            if (BeforeSend is { } beforeSend)
            {
                await beforeSend(message, cancellationToken);
            }

            await Mail.Writer.WriteAsync(message, cancellationToken);
        }
    }

    private sealed class RelayedEmailSender(IAccountEmailSender smtp, IAccountEmailSender recorder) : IAccountEmailSender
    {
        public async Task SendAsync(AccountEmail message, CancellationToken cancellationToken)
        {
            await smtp.SendAsync(message, cancellationToken);
            await recorder.SendAsync(message, cancellationToken);
        }
    }
}
