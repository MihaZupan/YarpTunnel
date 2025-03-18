using YarpTunnel.Backend;

namespace Microsoft.AspNetCore.Http;

public static class TunnelHttpContextExtensions
{
    public static bool IsTunnelRequest(this HttpContext context)
    {
        return context.Connection.Id.StartsWith(HttpClientConnectionContext.ConnectionIdPrefix, StringComparison.Ordinal);
    }
}
