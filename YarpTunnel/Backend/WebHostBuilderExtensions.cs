using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using YarpTunnel.Backend;

namespace Microsoft.Extensions.DependencyInjection;

public static class WebHostBuilderTunnelExtensions
{
    public static IWebHostBuilder UseTunnelTransport(this IWebHostBuilder hostBuilder, string url, Action<TunnelOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(url);

        var uri = new Uri(url, UriKind.Absolute);

        if (uri.Scheme != "https")
        {
            throw new ArgumentException("Tunnel connection must use HTTPS.", nameof(url));
        }

        hostBuilder.ConfigureKestrel(options =>
        {
            options.Listen(new UriEndPoint(uri));
        });

        return hostBuilder.ConfigureServices(services =>
        {
            var options = new TunnelOptions();
            configure?.Invoke(options);

            services.AddSingleton<IConnectionListenerFactory>(new TunnelConnectionListenerFactory(options));
        });
    }
}