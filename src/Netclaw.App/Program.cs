using Akka.Hosting;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Configuration;
using Netclaw.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.App;
using OllamaSharp;

// -- CLI mode selection --
string? headlessPrompt = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] is "-p" or "--prompt" && i + 1 < args.Length)
    {
        headlessPrompt = args[i + 1];
        break;
    }
}

var builder = Host.CreateApplicationBuilder(args);

// -- Netclaw paths (creates ~/.netclaw/ structure) --
var paths = new NetclawPaths();
paths.EnsureDirectoriesExist();
builder.Services.AddSingleton(paths);

// Load local overrides from ~/.netclaw/config/ (machine-specific, not in source control)
var localConfigPath = Path.Combine(paths.ConfigDirectory, "appsettings.Local.json");
builder.Configuration.AddJsonFile(localConfigPath, optional: true, reloadOnChange: false);

// Suppress all framework console logging — session logs go to disk,
// console is reserved for the chat UI
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// -- TimeProvider --
builder.Services.AddSingleton(TimeProvider.System);

// -- Ollama IChatClient --
var ollamaUrl = builder.Configuration["Ollama:Url"] ?? "http://localhost:11434";
var ollamaModel = builder.Configuration["Ollama:Model"] ?? "qwen3:30b";

builder.Services.AddSingleton<IChatClient>(
    new OllamaApiClient(new Uri(ollamaUrl), ollamaModel));

// -- Session configuration --
builder.Services.AddSingleton(new SessionConfig
{
    ModelId = ollamaModel,
    ContextWindowTokens = 32_768 // qwen3:30b default
});

// -- System prompt --
builder.Services.AddSingleton<ISystemPromptProvider>(
    new StaticSystemPromptProvider(
        "You are Netclaw, a helpful homelab operations assistant. Be concise and direct."));

// -- Tools --
var toolConfig = new ToolConfig();
builder.Services.AddSingleton(toolConfig);

var toolRegistry = new ToolRegistry();
toolRegistry.WithFirstPartyTools(toolConfig);
builder.Services.AddSingleton(toolRegistry);
builder.Services.AddSingleton<IToolExecutor>(new DispatchingToolExecutor(toolRegistry));

// -- Akka.NET actor system --
builder.Services.AddAkka("netclaw", (akkaBuilder, sp) =>
{
    akkaBuilder
        .ConfigureLoggers(setup =>
        {
            setup.ClearLoggers();
            setup.AddLoggerFactory();
            setup.LogLevel = Akka.Event.LogLevel.WarningLevel;
        })
        .WithInMemoryJournal()
        .WithInMemorySnapshotStore()
        .WithNetclawActors();
});

// -- Session pipeline (stream API for channels) --
builder.Services.AddSingleton<SessionPipeline>();

// -- Channel selection --
if (headlessPrompt is not null)
{
    builder.Services.AddSingleton<HeadlessChannel>(sp =>
        ActivatorUtilities.CreateInstance<HeadlessChannel>(sp, headlessPrompt));
    builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<HeadlessChannel>());
    builder.Services.AddSingleton<IChannel>(sp => sp.GetRequiredService<HeadlessChannel>());
}
else
{
    builder.Services.AddSingleton<ConsoleChannel>();
    builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ConsoleChannel>());
    builder.Services.AddSingleton<IChannel>(sp => sp.GetRequiredService<ConsoleChannel>());
}

await builder.Build().RunAsync();
