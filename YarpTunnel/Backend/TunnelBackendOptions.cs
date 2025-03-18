using Microsoft.AspNetCore.Connections;

namespace YarpTunnel.Backend;

public sealed class TunnelBackendOptions
{
    internal TunnelBackendOptions(UriEndPoint endPoint)
    {
        EndPoint = endPoint;
    }

    public UriEndPoint EndPoint { get; }

    /// <summary>
    /// Note that this is the number of HTTP/2 connections, so the parallel number of streams can be much higher.
    /// </summary>
    public int MaxConnectionCount { get; set; } = 10;

    public string? AuthorizationHeaderValue { get; set; }
}