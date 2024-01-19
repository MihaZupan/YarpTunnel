var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseTunnelTransport("https://localhost:7108/_yarp-tunnel?host=backend1", options =>
{
    options.AuthorizationHeaderValue = "Secret";
});

var app = builder.Build();

app.MapGet("/", (HttpContext context) => $"Hello World on connection {context.Connection.Id}!");

app.Run();
