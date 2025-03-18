using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Forwarder;

namespace YarpTunnel.Frontend;

public sealed class TunnelFrontendOptions
{
    internal TunnelFrontendOptions() { }

    /// <summary>
    /// Replacement for <see cref="ReverseProxyServiceCollectionExtensions.ConfigureHttpClient(IReverseProxyBuilder, Action{ForwarderHttpClientContext, SocketsHttpHandler})"/>.
    /// </summary>
    public Action<ForwarderHttpClientContext, SocketsHttpHandler>? ConfigureHttpClient { get; set; }
}
