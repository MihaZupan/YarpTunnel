using Microsoft.Net.Http.Headers;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddTunnelServices();

var app = builder.Build();

app.UseAuthorization();

app.MapHttp2Tunnel("/_yarp-tunnel")
    // Very basic auth for the tunnel endpoint.
    // Replace this with something reasonable :)
    .Add(builder =>
    {
        var previous = builder.RequestDelegate;

        builder.RequestDelegate = async context =>
        {
            if (!context.Request.Headers.TryGetValue(HeaderNames.Authorization, out var token) ||
                token.Count != 1 ||
                !ValidateTunnelAuthorizationHeader(token.ToString()))
            {
                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                return;
            }

            if (previous is not null)
            {
                await previous(context);
            }

            static bool ValidateTunnelAuthorizationHeader(string value)
            {
                string expected = "Secret";

                return Equals(value, expected);
            }

            static bool Equals(string a, string b)
            {
                if (a.Length != b.Length) return false;

                int diff = 0;

                for (int i = 0; i < a.Length; i++)
                {
                    diff |= a[i] ^ b[i];
                }

                return diff == 0;
            }

        };
    });

app.MapReverseProxy();

app.Run();