using System.Net;
using chat.Api.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace chat.IntegrationTests;

public sealed class ProductionTransportTests
{
    [Fact]
    public async Task DirectHttpRejectsCredentialsAndIgnoresForwardedHeaders()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var production = factory.WithWebHostBuilder(TransportOnly);
        var denied = await production.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Request.Method = "POST";
            context.Request.Path = "/v1/auth/login";
            context.Request.Scheme = "http";
            context.Request.Headers.Authorization = "Bearer synthetic";
            context.Request.Headers["X-Forwarded-For"] = "192.0.2.1";
            context.Request.Headers["X-Forwarded-Proto"] = "https";
        });
        Assert.Equal(403, denied.Response.StatusCode);
        Assert.Equal(0, denied.Response.Headers.Location.Count);

        using var client = production.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        using var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Theory]
    [InlineData("192.0.2.10", "203.0.113.2", "https", "https", 200)]
    [InlineData("::ffff:192.0.2.10", "203.0.113.2", "https", "https", 200)]
    [InlineData("192.0.2.10", "203.0.113.2", "https", "http", 403)]
    [InlineData("192.0.2.11", "203.0.113.2", "https", "http", 403)]
    [InlineData("192.0.2.11", "203.0.113.2", "https", "https", 403)]
    [InlineData("192.0.2.10", "203.0.113.2", "http", "https", 403)]
    [InlineData("192.0.2.10", "203.0.113.2, 203.0.113.3", "https", "https", 403)]
    [InlineData("192.0.2.10", "203.0.113.2", "https, https", "https", 403)]
    [InlineData("192.0.2.10", null, null, "https", 403)]
    public async Task ProxyModeRequiresExactPeerAndSingleForwardedHttps(string peer, string? clientIp, string? proto,
        string scheme, int status)
    {
        using var factory = new WebApplicationFactory<Program>();
        using var production = factory.WithWebHostBuilder(builder =>
        {
            TransportOnly(builder);
            builder.UseSetting("ReverseProxy:KnownProxies:0", "192.0.2.10");
        });
        var response = await production.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            context.Request.Path = "/health/live";
            context.Request.Scheme = scheme;
            if (clientIp is not null) context.Request.Headers["X-Forwarded-For"] = clientIp;
            if (proto is not null) context.Request.Headers["X-Forwarded-Proto"] = proto;
        });

        Assert.Equal(status, response.Response.StatusCode);
        if (status == 200)
        {
            Assert.Equal(clientIp, response.Connection.RemoteIpAddress?.ToString());
            Assert.Equal("https", response.Request.Scheme);
        }
    }

    private static void TransportOnly(IWebHostBuilder builder) => builder.UseEnvironment("Production")
        .ConfigureServices(services =>
        {
            var guard = services.Single(item => item.ServiceType == typeof(IHostedService)
                && item.ImplementationType == typeof(SingleReplicaGuard));
            services.Remove(guard);
        });
}
