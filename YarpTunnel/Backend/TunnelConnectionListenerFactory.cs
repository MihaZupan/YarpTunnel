using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace YarpTunnel.Backend;

internal sealed class TunnelConnectionListenerFactory : IConnectionListenerFactory, IConnectionListenerFactorySelector
{
    private readonly TunnelBackendOptions _options;
    private readonly ILoggerFactory _loggerFactory;

    public TunnelConnectionListenerFactory(TunnelBackendOptions options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _loggerFactory = loggerFactory;
    }

    public bool CanBind(EndPoint endpoint)
    {
        return ReferenceEquals(_options.EndPoint, endpoint);
    }

    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        return new(new TunnelConnectionListener(_options, endpoint, _loggerFactory));
    }
}