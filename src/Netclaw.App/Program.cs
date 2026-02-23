using Akka.Hosting;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Tools;
using Netclaw.App;
using Netclaw.App.Configuration;
using Netclaw.Channels;
using Netclaw.Configuration;

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

// -- Layered configuration chain --
// 1. netclaw.json (base config, optional)
// 2. secrets.json (credentials overlay, optional)
// 3. NETCLAW_* environment variables (highest priority)
builder.Configuration
    .AddJsonFile(paths.NetclawConfigPath, optional: true, reloadOnChange: false)
    .AddJsonFile(paths.SecretsPath, optional: true, reloadOnChange: false)
    .AddEnvironmentVariables("NETCLAW_");

// Suppress all framework console logging — session logs go to disk,
// console is reserved for the chat UI
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// -- TimeProvider --
builder.Services.AddSingleton(TimeProvider.System);

// -- Providers and models --
var providers = builder.Configuration.GetSection("Providers")
    .Get<Dictionary<string, ProviderEntry>>()
    ?? new() { ["local-ollama"] = new ProviderEntry() };
var models = builder.Configuration.GetSection("Models")
    .Get<ModelSelection>() ?? new ModelSelection();

var factory = new ChatClientFactory(providers);
var clientProvider = new NetclawChatClientProvider(factory, models);
builder.Services.AddSingleton<IChatClientProvider>(clientProvider);

// -- Session config from resolved models --
var sessionSection = builder.Configuration.GetSection("Session");
builder.Services.AddSingleton(new SessionConfig
{
    ModelId = models.Main.ModelId,
    ContextWindowTokens = models.Main.ContextWindow ?? 32_768,
    CompactionModelId = models.Compaction?.ModelId,
    CompactionThreshold = sessionSection.GetValue("CompactionThreshold", 0.75),
    SnapshotInterval = sessionSection.GetValue("SnapshotInterval", 20),
    KeepRecentToolResults = sessionSection.GetValue("KeepRecentToolResults", 3),
    MaxToolIterationsPerTurn = sessionSection.GetValue("MaxToolIterationsPerTurn", 10),
});

// -- Tools (auto-bound, no required properties) --
var toolConfig = builder.Configuration.GetSection("Tools")
    .Get<ToolConfig>() ?? new ToolConfig();
builder.Services.AddSingleton(toolConfig);

var toolRegistry = new ToolRegistry();
toolRegistry.WithFirstPartyTools(toolConfig);
builder.Services.AddSingleton(toolRegistry);
builder.Services.AddSingleton<IToolExecutor>(new DispatchingToolExecutor(toolRegistry));

// -- System prompt (file-based, with first-run seed) --
if (!File.Exists(paths.PersonalityPath))
    File.WriteAllText(paths.PersonalityPath,
        "You are Netclaw, a helpful homelab operations assistant. "
        + "Be concise and direct.");
builder.Services.AddSingleton<ISystemPromptProvider>(
    new FileSystemPromptProvider(paths));

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
