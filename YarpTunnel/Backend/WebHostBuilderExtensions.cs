using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using YarpTunnel.Backend;

namespace Microsoft.Extensions.DependencyInjection;

public static class WebHostBuilderTunnelExtensions
{
    public static IWebHostBuilder UseTunnelTransport(this IWebHostBuilder hostBuilder, string url, Action<TunnelBackendOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(url);

        var uri = new Uri(url, UriKind.Absolute);

        if (uri.Scheme != "https")
        {
            throw new ArgumentException("Tunnel connection must use HTTPS.", nameof(url));
        }

        var endPoint = new UriEndPoint(uri);

        hostBuilder.ConfigureKestrel(options =>
        {
            options.Listen(endPoint, options =>
            {
                options.Protocols = HttpProtocols.Http2;
            });
        });

        return hostBuilder.ConfigureServices(services =>
        {
            var options = new TunnelBackendOptions(endPoint);
            configure?.Invoke(options);

            var loggerFactory = services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();

            services.AddSingleton<IConnectionListenerFactory>(new TunnelConnectionListenerFactory(options, loggerFactory));
        });
    }
}