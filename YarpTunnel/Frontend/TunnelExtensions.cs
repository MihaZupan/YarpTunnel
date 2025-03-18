using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using Yarp.ReverseProxy.Forwarder;
using YarpTunnel.Frontend;

namespace Microsoft.Extensions.DependencyInjection;

public static class TunnelExtensions
{
    public static IServiceCollection AddTunnelServices(this IServiceCollection services, Action<TunnelFrontendOptions>? configure = null)
    {
        var options = new TunnelFrontendOptions();
        configure?.Invoke(options);

        var tunnelFactory = new TunnelClientFactory(options);
        services.AddSingleton(tunnelFactory);
        services.AddSingleton<IForwarderHttpClientFactory>(tunnelFactory);

        return services;
    }

    public static IEndpointConventionBuilder MapTunnel(this IEndpointRouteBuilder routes, string path)
    {
        return routes.MapGet(path, static async (HttpContext context, [FromQuery] string host, TunnelClientFactory tunnelFactory, IHostApplicationLifetime lifetime, ILoggerFactory loggerFactory) =>
        {
            if (string.IsNullOrWhiteSpace(host) ||
                !ProductHeaderValue.TryParse(context.Request.Headers.Upgrade, out var upgrade) ||
                upgrade.Name != "YarpTunnel" ||
                !Version.TryParse(upgrade.Version, out var version) ||
                version < new Version(0, 1, 5) ||
                context.Features.Get<IHttpUpgradeFeature>() is not { IsUpgradableRequest: true } upgradeFeature)
            {
                return Results.BadRequest();
            }

            await using Stream transport = await upgradeFeature.UpgradeAsync();

            DisableRequestLimits(context);

            if (!host.EndsWith(".tunnel", StringComparison.OrdinalIgnoreCase))
            {
                host += ".tunnel";
            }

            await transport.WriteAsync("YarpTunnel"u8.ToArray(), context.RequestAborted);

            var stream = new TunnelTransportStream(transport);

            tunnelFactory.AddConnection(host, stream);

            using var _ = context.RequestAborted.UnsafeRegister(static s => ((Stream)s!).Dispose(), stream);
            using var __ = lifetime.ApplicationStopping.UnsafeRegister(static s => ((Stream)s!).Dispose(), stream);

            await stream.DisposedTask;

            return Results.Empty;
        });
    }

    private static void DisableRequestLimits(HttpContext httpContext)
    {
        var minRequestBodyDataRateFeature = httpContext.Features.Get<IHttpMinRequestBodyDataRateFeature>();
        if (minRequestBodyDataRateFeature is not null)
        {
            minRequestBodyDataRateFeature.MinDataRate = null;
        }

        var maxRequestBodySizeFeature = httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (maxRequestBodySizeFeature is not null)
        {
            if (!maxRequestBodySizeFeature.IsReadOnly)
            {
                maxRequestBodySizeFeature.MaxRequestBodySize = null;
            }
        }

        httpContext.Features.Get<IHttpRequestTimeoutFeature>()?.DisableTimeout();
    }
}