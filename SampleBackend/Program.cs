var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseTunnelTransport("https://localhost:7108/_yarp-tunnel?host=backend1", options =>
{
    options.AuthorizationHeaderValue = "Secret";
    options.MaxConnectionCount = 2;
});

builder.WebHost.UseKestrel(options =>
{
    options.ListenAnyIP(1234);
});

var app = builder.Build();

app.MapGet("/", (HttpContext context) =>
{
    if (context.IsTunnelRequest())
    {
        return $"Hello World on tunnel connection {context.Connection.Id}!";
    }

    return $"Hello World on regular connection {context.Connection.Id}!";
});

app.Run();