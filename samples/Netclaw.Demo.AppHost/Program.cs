// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Json;
using Aspire.Hosting.ApplicationModel;
using Netclaw.Channels.Mattermost.Bootstrap;
using Netclaw.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

// Sandbox NetClaw state under .demo-home/.netclaw so a host-installed
// NetClaw at ~/.netclaw/ is untouched. NETCLAW_HOME re-roots only
// NetClaw's own state; the other SpecialFolder.UserProfile callsites
// in NetClaw (real Chrome install, real ~/.claude/skills, etc.) are
// intentionally NOT redirected.
var demoHome = Path.GetFullPath(
    Path.Combine(builder.AppHostDirectory, ".demo-home", ".netclaw"));

// Resolve the Aspire dashboard's OTLP endpoint up-front. Reading from
// ctx.EnvironmentVariables in the daemon's env-var callback doesn't
// work: Aspire stores OTEL_EXPORTER_OTLP_ENDPOINT as an IValueProvider
// reference that's resolved after our callback runs, so an `is string`
// check silently misses. Process env (set in launchSettings.json) IS a
// real string at this point — read it here and inject statically.
var aspireOtlpEndpoint =
    builder.Configuration["DOTNET_DASHBOARD_OTLP_ENDPOINT_URL"]
    ?? builder.Configuration["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"];

var demoProfile = ResolveDemoProfile(
    Environment.GetEnvironmentVariable("NETCLAW_DEMO_PROFILE"));

// Opt-in to a host-running Ollama so warm-cache operators don't re-pull
// the model into a fresh container volume on every demo run.
var useHostOllama = string.Equals(
    Environment.GetEnvironmentVariable("NETCLAW_DEMO_USE_HOST_OLLAMA"),
    "1",
    StringComparison.Ordinal);
var modelId = Environment.GetEnvironmentVariable("NETCLAW_DEMO_MODEL_ID")
    ?? "qwen3.5:2b-q4_K_M";
var hostOllamaUrl = Environment.GetEnvironmentVariable("NETCLAW_DEMO_OLLAMA_URL")
    ?? "http://127.0.0.1:11434";

IResourceBuilder<OllamaModelResource>? aspireOllamaModel = null;
Func<CancellationToken, ValueTask<string>> resolveOllamaUrl;

if (useHostOllama)
{
    resolveOllamaUrl = _ => ValueTask.FromResult(hostOllamaUrl.TrimEnd('/'));
}
else
{
    var ollama = builder.AddOllama("ollama").WithDataVolume();
    if (demoProfile == DemoProfile.Fast)
    {
        ollama = ollama
            .WithEnvironment("OLLAMA_NUM_PARALLEL", "1")
            .WithEnvironment("OLLAMA_MAX_LOADED_MODELS", "1")
            .WithEnvironment("OLLAMA_FLASH_ATTENTION", "1")
            .WithEnvironment("OLLAMA_KV_CACHE_TYPE", "q8_0")
            .WithEnvironment("OLLAMA_KEEP_ALIVE", "-1");
    }

    aspireOllamaModel = ollama.AddModel("ollama-model", modelId);
    resolveOllamaUrl = async ct =>
    {
        var url = await ollama.Resource.PrimaryEndpoint.GetValueAsync(ct);
        return url!.TrimEnd('/');
    };
}

// mattermost-preview ships DB + everything in a single image (no
// separate Postgres needed). Deprecated upstream — documented in the
// README as a future migration. Testing-mode env vars come from the
// bootstrap library so the fixture and the demo share one source.
var mattermost = builder.AddContainer("mattermost", "mattermost/mattermost-preview")
    .WithHttpEndpoint(targetPort: 8065, name: "web");
foreach (var (envName, envValue) in MattermostBootstrapper.DefaultEnvironmentVariables)
    mattermost = mattermost.WithEnvironment(envName, envValue);

var bootstrapResult = new TaskCompletionSource<BootstrapResult>(
    TaskCreationOptions.RunContinuationsAsynchronously);

builder.Eventing.Subscribe<ResourceReadyEvent>(mattermost.Resource, async (evt, ct) =>
{
    // ResourceReadyEvent can fire more than once if Mattermost is
    // restarted mid-session. The seed sequence is NOT idempotent —
    // creating the admin user a second time throws because it already
    // exists. First-ready wins; subsequent fires are ignored.
    if (bootstrapResult.Task.IsCompleted)
        return;

    var serverUrl = new Uri(mattermost.GetEndpoint("web").Url);
    try
    {
        var result = await MattermostBootstrapper.SeedAsync(serverUrl, new BootstrapOptions(), ct);
        bootstrapResult.TrySetResult(result);
    }
    catch (Exception ex)
    {
        bootstrapResult.TrySetException(ex);
        throw;
    }
});

var daemon = builder.AddProject<Projects.Netclaw_Daemon>("daemon")
    .WithEnvironment("NETCLAW_HOME", demoHome)
    .WithEnvironment("NETCLAW_Daemon__Host", "127.0.0.1")
    .WithEnvironment("NETCLAW_Daemon__Port", "5299")
    .WithEnvironment("NETCLAW_Daemon__ExposureMode", "local")
    // Framework timeout defaults assume hosted/GPU inference; CPU
    // prefill on a tool-armed system prompt can blow through 10 min.
    .WithEnvironment("NETCLAW_Session__TurnLlmTimeoutSeconds", "1800")
    .WithEnvironment("NETCLAW_Session__FirstTokenTimeoutSeconds", "1800")
    .WithEnvironment("NETCLAW_Session__PrefillTimeoutSeconds", "1800");

if (demoProfile == DemoProfile.Fast)
{
    daemon = daemon
        .WithEnvironment("NETCLAW_Session__MaxToolIterationsPerTurn", "1")
        .WithEnvironment("NETCLAW_Providers__ollama__VendorOptions__DisableThinking", "true");
}

// Opt the daemon into OTLP export against the Aspire dashboard's OTLP
// receiver. NetClaw's telemetry is opt-in by design (Enabled defaults
// false), so the AppHost has to explicitly turn it on AND point at the
// right endpoint — otherwise logs/metrics never reach the dashboard's
// Structured Logs / Metrics views.
if (!string.IsNullOrWhiteSpace(aspireOtlpEndpoint))
{
    daemon = daemon
        .WithEnvironment("NETCLAW_Telemetry__Enabled", "true")
        .WithEnvironment("NETCLAW_Telemetry__Otlp__Endpoint", aspireOtlpEndpoint);
}

daemon = daemon
    .WithEnvironment(async ctx =>
    {
        var result = await bootstrapResult.Task.WaitAsync(ctx.CancellationToken);
        ctx.EnvironmentVariables["NETCLAW_Mattermost__Enabled"] = "true";
        ctx.EnvironmentVariables["NETCLAW_Mattermost__ServerUrl"] = result.ServerUrl.ToString();
        ctx.EnvironmentVariables["NETCLAW_Mattermost__BotToken"] = result.Bot.Token;
        ctx.EnvironmentVariables["NETCLAW_Mattermost__DefaultChannelId"] = result.ChannelId;
        // The fast demo keeps the seeded channel on Public so the first
        // turn stays lean: no full personal prompt overlay, no memory/
        // skill/subagent catalogs, and a much smaller visible tool surface.
        // The full profile restores the heavier personal-audience demo.
        ctx.EnvironmentVariables[$"NETCLAW_Mattermost__ChannelAudiences__{result.ChannelId}"] =
            (demoProfile == DemoProfile.Fast ? TrustAudience.Public : TrustAudience.Personal).ToWireValue();
        // CallbackUrl deliberately unset — the host-process daemon
        // can't be reached from the Mattermost container without
        // poking holes in ExposureMode.Local, so approvals fall back
        // to text-reply mode.
        ctx.EnvironmentVariables["NETCLAW_Mattermost__MentionOnly"] = "false";
        ctx.EnvironmentVariables["NETCLAW_Mattermost__AllowDirectMessages"] = "true";

        // NetClaw's Providers section is a dictionary keyed by
        // provider name — Models__Main__Provider has to match the key
        // segment used here (lowercase "ollama").
        var ollamaUrl = await resolveOllamaUrl(ctx.CancellationToken);
        if (demoProfile == DemoProfile.Fast)
            await PrewarmOllamaAsync(ollamaUrl, modelId, ctx.CancellationToken);

        ctx.EnvironmentVariables["NETCLAW_Providers__ollama__Type"] = "ollama";
        ctx.EnvironmentVariables["NETCLAW_Providers__ollama__Endpoint"] = ollamaUrl;
        ctx.EnvironmentVariables["NETCLAW_Providers__ollama__AuthMethod"] = "None";
        ctx.EnvironmentVariables["NETCLAW_Models__Main__Provider"] = "ollama";
        ctx.EnvironmentVariables["NETCLAW_Models__Main__ModelId"] = modelId;
    })
    .WaitFor(mattermost);

if (aspireOllamaModel is not null)
    daemon.WaitFor(aspireOllamaModel);

builder.Build().Run();

static DemoProfile ResolveDemoProfile(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw))
        return DemoProfile.Fast;

    return raw.Trim().ToLowerInvariant() switch
    {
        "fast" => DemoProfile.Fast,
        "full" => DemoProfile.Full,
        _ => throw new InvalidOperationException(
            "NETCLAW_DEMO_PROFILE must be 'fast' or 'full'.")
    };
}

static async Task PrewarmOllamaAsync(string ollamaUrl, string modelId, CancellationToken cancellationToken)
{
    using var http = new HttpClient
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    using var response = await http.PostAsJsonAsync(
        $"{ollamaUrl.TrimEnd('/')}/api/chat",
        new
        {
            model = modelId,
            messages = Array.Empty<object>(),
            stream = false,
            keep_alive = -1
        },
        cancellationToken);

    response.EnsureSuccessStatusCode();
}

enum DemoProfile
{
    Fast,
    Full
}
