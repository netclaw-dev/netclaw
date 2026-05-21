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

var ollama = builder.AddOllama("ollama")
    .WithDataVolume();
var qwen = ollama.AddModel("qwen3:4b");

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

builder.AddProject<Projects.Netclaw_Daemon>("daemon")
    .WithEnvironment("NETCLAW_HOME", demoHome)
    .WithEnvironment("NETCLAW_Daemon__Host", "127.0.0.1")
    .WithEnvironment("NETCLAW_Daemon__Port", "5299")
    .WithEnvironment("NETCLAW_Daemon__ExposureMode", "local")
    .WithEnvironment(async ctx =>
    {
        var result = await bootstrapResult.Task.WaitAsync(ctx.CancellationToken);
        ctx.EnvironmentVariables["NETCLAW_Mattermost__Enabled"] = "true";
        ctx.EnvironmentVariables["NETCLAW_Mattermost__ServerUrl"] = result.ServerUrl.ToString();
        ctx.EnvironmentVariables["NETCLAW_Mattermost__BotToken"] = result.Bot.Token;
        ctx.EnvironmentVariables["NETCLAW_Mattermost__DefaultChannelId"] = result.ChannelId;
        // CallbackUrl deliberately unset — the host-process daemon
        // can't be reached from the Mattermost container without
        // poking holes in ExposureMode.Local, so approvals fall back
        // to text-reply mode.
        ctx.EnvironmentVariables["NETCLAW_Mattermost__MentionOnly"] = "false";
        ctx.EnvironmentVariables["NETCLAW_Mattermost__AllowDirectMessages"] = "true";

        // NetClaw's Providers section is a dictionary keyed by
        // provider name — Models__Main__Provider has to match the key
        // segment used here (lowercase "ollama").
        var ollamaUrl = await ollama.Resource.PrimaryEndpoint
            .GetValueAsync(ctx.CancellationToken);
        ctx.EnvironmentVariables["NETCLAW_Providers__ollama__Type"] = "ollama";
        ctx.EnvironmentVariables["NETCLAW_Providers__ollama__Endpoint"] = ollamaUrl!;
        ctx.EnvironmentVariables["NETCLAW_Providers__ollama__AuthMethod"] = "None";
        ctx.EnvironmentVariables["NETCLAW_Models__Main__Provider"] = "ollama";
        ctx.EnvironmentVariables["NETCLAW_Models__Main__ModelId"] = "qwen3:4b";
    })
    .WaitFor(mattermost)
    .WaitFor(qwen);

builder.Build().Run();
