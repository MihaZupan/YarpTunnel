using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Extensions.Hosting;
using Yarp.ReverseProxy.Forwarder;
using YarpTunnel.Frontend;

namespace Microsoft.Extensions.DependencyInjection;

public static class TunnelExensions
{
    public static IServiceCollection AddTunnelServices(this IServiceCollection services)
    {
        var tunnelFactory = new TunnelClientFactory();
        services.AddSingleton(tunnelFactory);
        services.AddSingleton<IForwarderHttpClientFactory>(tunnelFactory);
        return services;
    }

    public static IEndpointConventionBuilder MapHttp2Tunnel(this IEndpointRouteBuilder routes, string path)
    {
        return routes.MapPost(path, static async (HttpContext context, [FromQuery] string host, TunnelClientFactory tunnelFactory, IHostApplicationLifetime lifetime) =>
        {
            // HTTP/2 duplex stream
            if (context.Request.Protocol != HttpProtocol.Http2 || string.IsNullOrEmpty(host))
            {
                return Results.BadRequest();
            }

            DisableMinRequestBodyDataRateAndMaxRequestBodySize(context);

            if (!host.EndsWith(".tunnel", StringComparison.OrdinalIgnoreCase))
            {
                host += ".tunnel";
            }

            await using var stream = new DuplexHttpStream(context);

            tunnelFactory.AddConnection(host, stream);

            using var _ = context.RequestAborted.UnsafeRegister(static s => ((Stream)s!).Dispose(), stream);
            using var __ = lifetime.ApplicationStopping.UnsafeRegister(static s => ((Stream)s!).Dispose(), stream);

            await stream.DisposedTask;

            return Results.Empty;
        });
    }

    private static void DisableMinRequestBodyDataRateAndMaxRequestBodySize(HttpContext httpContext)
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
    }
}