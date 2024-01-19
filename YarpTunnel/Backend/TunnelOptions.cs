namespace YarpTunnel.Backend;

public sealed class TunnelOptions
{
    public int MaxConnectionCount { get; set; } = 10;

    public string? AuthorizationHeaderValue { get; set; }
}