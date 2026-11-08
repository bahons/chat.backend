using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace chat.Api.DependencyInjection;

internal static class ProductionTransport
{
    public static void UseProductionTransport(this WebApplication app)
    {
        if (!app.Environment.IsProduction())
        {
            if (!app.Environment.IsDevelopment())
            {
                app.UseHsts();
                app.UseHttpsRedirection();
            }

            return;
        }

        if (app.Configuration.GetValue<bool>("ForwardedHeaders_Enabled"))
        {
            throw new InvalidOperationException("Configure ReverseProxy:KnownProxies instead of automatic forwarded headers.");
        }

        app.Use(async (context, next) =>
        {
            if (!context.Request.IsHttps)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context);
        });

        var proxyAddresses = app.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [];
        if (proxyAddresses.Length > 0)
        {
            var options = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
                ForwardLimit = 1,
                RequireHeaderSymmetry = true
            };
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();

            var trustedPeers = new HashSet<IPAddress>();
            foreach (var configured in proxyAddresses)
            {
                if (!IPAddress.TryParse(configured, out var address))
                {
                    throw new InvalidOperationException("ReverseProxy:KnownProxies must contain exact IP addresses.");
                }

                var canonical = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
                if (canonical.Equals(IPAddress.Any) || canonical.Equals(IPAddress.IPv6Any))
                {
                    throw new InvalidOperationException("ReverseProxy:KnownProxies requires specific peer addresses.");
                }

                options.KnownProxies.Add(canonical);
                trustedPeers.Add(canonical.MapToIPv6());
            }

            app.Use(async (context, next) =>
            {
                var peer = context.Connection.RemoteIpAddress;
                var forwardedFor = context.Request.Headers["X-Forwarded-For"];
                var forwardedProto = context.Request.Headers["X-Forwarded-Proto"];
                if (peer is null || !trustedPeers.Contains(peer.MapToIPv6())
                    || forwardedFor.Count != 1 || !IPAddress.TryParse(forwardedFor[0], out _)
                    || forwardedProto.Count != 1 || forwardedProto[0] != "https")
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                await next(context);
            });
            app.UseForwardedHeaders(options);
        }

        app.UseHsts();
    }
}
