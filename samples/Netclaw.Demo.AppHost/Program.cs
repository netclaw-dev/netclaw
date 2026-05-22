// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Aspire.Hosting.ApplicationModel;
using Netclaw.Channels.Mattermost.Bootstrap;

var builder = DistributedApplication.CreateBuilder(args);

// Sandbox NetClaw state under .demo-home/.netclaw so a host-installed
// NetClaw at ~/.netclaw/ is untouched. NETCLAW_HOME re-roots only
// NetClaw's own state; the other SpecialFolder.UserProfile callsites
// in NetClaw (real Chrome install, real ~/.claude/skills, etc.) are
// intentionally NOT redirected.
var demoHome = Path.GetFullPath(
    Path.Combine(builder.AppHostDirectory, ".demo-home", ".netclaw"));

// LLM provider knobs. Defaults pull and run qwen3:4b inside an
// Aspire-managed Ollama container -- zero-setup for first-run demos.
// Operators with an existing Ollama instance (or a faster model already
// pulled) can opt out via NETCLAW_DEMO_USE_HOST_OLLAMA=1, in which case
// the daemon talks directly to NETCLAW_DEMO_OLLAMA_URL (default
// http://127.0.0.1:11434) using NETCLAW_DEMO_MODEL_ID (default
// qwen3:4b). This is the difference between "kick the tires from a
// cold cache" and "I already have Ollama running, let's go."
var useHostOllama = string.Equals(
    Environment.GetEnvironmentVariable("NETCLAW_DEMO_USE_HOST_OLLAMA"),
    "1",
    StringComparison.Ordinal);
var modelId = Environment.GetEnvironmentVariable("NETCLAW_DEMO_MODEL_ID")
    ?? "qwen3:4b";
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
    aspireOllamaModel = ollama.AddModel(modelId);
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
    // Generous LLM timeouts. NetClaw's defaults (10 min first-token,
    // 30 min prefill) assume a hosted/GPU provider. Local CPU
    // inference on a small machine can blow through 10 min on prefill
    // alone for a tool-armed system prompt. Bump everything to 30 min
    // so a slow local run can still complete a round trip.
    .WithEnvironment("NETCLAW_Session__TurnLlmTimeoutSeconds", "1800")
    .WithEnvironment("NETCLAW_Session__FirstTokenTimeoutSeconds", "1800")
    .WithEnvironment("NETCLAW_Session__PrefillTimeoutSeconds", "1800")
    .WithEnvironment(async ctx =>
    {
        var result = await bootstrapResult.Task.WaitAsync(ctx.CancellationToken);
        ctx.EnvironmentVariables["NETCLAW_Mattermost__Enabled"] = "true";
        ctx.EnvironmentVariables["NETCLAW_Mattermost__ServerUrl"] = result.ServerUrl.ToString();
        ctx.EnvironmentVariables["NETCLAW_Mattermost__BotToken"] = result.Bot.Token;
        ctx.EnvironmentVariables["NETCLAW_Mattermost__DefaultChannelId"] = result.ChannelId;
        // Mark the seeded channel as "personal" audience. The default
        // public audience blocks list_reminders / set_reminder / most
        // memory and file tools — which makes the bot exhaust its
        // tool-call budget on policy denials before it ever produces
        // a chat reply. For a single-operator local demo, "personal"
        // is the right trust posture: the operator IS the only user.
        ctx.EnvironmentVariables[$"NETCLAW_Mattermost__ChannelAudiences__{result.ChannelId}"] = "personal";
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
        ctx.EnvironmentVariables["NETCLAW_Providers__ollama__Type"] = "ollama";
        ctx.EnvironmentVariables["NETCLAW_Providers__ollama__Endpoint"] = ollamaUrl;
        ctx.EnvironmentVariables["NETCLAW_Providers__ollama__AuthMethod"] = "None";
        ctx.EnvironmentVariables["NETCLAW_Models__Main__Provider"] = "ollama";
        ctx.EnvironmentVariables["NETCLAW_Models__Main__ModelId"] = modelId;

        // Bridge Aspire's standard OTLP env vars into the daemon's
        // NetClaw-specific Telemetry config so the daemon actually
        // exports logs/metrics back to the Aspire dashboard. Without
        // this, NETCLAW_Telemetry__Enabled defaults to false and the
        // daemon's OTel pipeline drops the OTLP exporter entirely.
        if (ctx.EnvironmentVariables.TryGetValue("OTEL_EXPORTER_OTLP_ENDPOINT", out var otlpEndpointObj)
            && otlpEndpointObj is string otlpEndpoint
            && !string.IsNullOrEmpty(otlpEndpoint))
        {
            ctx.EnvironmentVariables["NETCLAW_Telemetry__Enabled"] = "true";
            ctx.EnvironmentVariables["NETCLAW_Telemetry__Otlp__Endpoint"] = otlpEndpoint;
        }
    })
    .WaitFor(mattermost);

if (aspireOllamaModel is not null)
    daemon.WaitFor(aspireOllamaModel);

builder.Build().Run();
