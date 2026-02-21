using Akka.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Netclaw");
logger.LogInformation("Netclaw web host scaffold ready (.NET {Runtime})", Environment.Version.ToString());
logger.LogInformation("Akka.Agents marker loaded: {Marker}", nameof(AkkaAgentsAssemblyMarker));

app.MapGet("/", () => Results.Ok(new
{
    service = "netclaw",
    mode = "mvp-scaffold",
    managementUi = "planned"
}));

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

app.MapGet("/health/ready", () => Results.Ok(new
{
    status = "ready",
    slackTransport = "socket-mode",
    mcp = "planned"
}));

app.MapGet("/api/runtime/info", () => Results.Ok(new
{
    runtime = Environment.Version.ToString(),
    framework = nameof(AkkaAgentsAssemblyMarker)
}));

await app.RunAsync();
