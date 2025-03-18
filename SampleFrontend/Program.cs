using Microsoft.Net.Http.Headers;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddTunnelServices();

var app = builder.Build();

app.UseAuthorization();

app.MapTunnel("/_yarp-tunnel")
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

                return expected.Length == value.Length &&
                    CryptographicOperations.FixedTimeEquals(
                        MemoryMarshal.AsBytes(expected.AsSpan()),
                        MemoryMarshal.AsBytes(value.AsSpan()));
            }
        };
    });

app.MapReverseProxy();

app.Run();