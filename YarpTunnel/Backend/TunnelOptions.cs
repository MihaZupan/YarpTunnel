using Microsoft.AspNetCore.Connections;

namespace YarpTunnel.Backend;

public sealed class TunnelOptions
{
    internal TunnelOptions(UriEndPoint endPoint)
    {
        EndPoint = endPoint;
    }

    public UriEndPoint EndPoint { get; }

    public int MaxConnectionCount { get; set; } = 10;

    public string? AuthorizationHeaderValue { get; set; }
}