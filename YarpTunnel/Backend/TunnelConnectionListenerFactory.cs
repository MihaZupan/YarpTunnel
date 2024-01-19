using System.Net;
using Microsoft.AspNetCore.Connections;

namespace YarpTunnel.Backend;

internal sealed class TunnelConnectionListenerFactory : IConnectionListenerFactory
{
    private readonly TunnelOptions _options;

    public TunnelConnectionListenerFactory(TunnelOptions options)
    {
        _options = options;
    }

    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        return new(new TunnelConnectionListener(_options, endpoint));
    }
}