// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Aspire.Hosting.ApplicationModel;
using Netclaw.Channels.Mattermost.Bootstrap;

var builder = DistributedApplication.CreateBuilder(args);

// .demo-home lives next to this AppHost project. We sandbox the daemon's
// state under it via NETCLAW_HOME so a host-installed NetClaw at
// ~/.netclaw/ keeps its own SQLite, keys, secrets, and identity files
// untouched. The 8 other SpecialFolder.UserProfile callsites in NetClaw
// intentionally read the real operator home and are not redirected by
// NETCLAW_HOME — that asymmetry is correct, not a bug.
var demoHome = Path.GetFullPath(
    Path.Combine(builder.AppHostDirectory, ".demo-home", ".netclaw"));

// mattermost-preview ships DB + everything else in a single image; fine
// for a demo (deprecated upstream — documented in the README as a
// future migration to mattermost-team-edition + Postgres). Testing-mode
// env vars come from MattermostBootstrapper so the fixture and the demo
// share one source of truth.
var mattermost = builder.AddContainer("mattermost", "mattermost/mattermost-preview")
    .WithHttpEndpoint(targetPort: 8065, name: "web");
foreach (var (envName, envValue) in MattermostBootstrapper.DefaultEnvironmentVariables)
    mattermost = mattermost.WithEnvironment(envName, envValue);

// Bootstrap result is published by a ResourceReadyEvent subscription on
// the Mattermost resource and awaited by the daemon's env-var callback.
// The daemon then sees NETCLAW_Mattermost__BotToken etc. populated
// before its process starts, matching the Channels.Mattermost
// registration path's expectation that credentials exist at process
// start.
var bootstrapResult = new TaskCompletionSource<BootstrapResult>(
    TaskCreationOptions.RunContinuationsAsynchronously);

builder.Eventing.Subscribe<ResourceReadyEvent>(mattermost.Resource, async (evt, ct) =>
{
    // ResourceReadyEvent can fire more than once if Mattermost is
    // restarted mid-session (manually via the dashboard, or via a
    // resource-restart command). The seed sequence is NOT idempotent —
    // creating the admin user a second time throws because it already
    // exists. We accept the first ready signal and ignore subsequent
    // ones; an operator who really needs a clean reseed should
    // Ctrl+C the AppHost and remove the Mattermost container.
    if (bootstrapResult.Task.IsCompleted)
        return;

    var endpoint = mattermost.GetEndpoint("web");
    var serverUrl = new Uri(endpoint.Url);
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
        // Trim trailing slash so Mattermost.NET doesn't end up with `//api/...`.
        ctx.EnvironmentVariables["NETCLAW_Mattermost__ServerUrl"] =
            result.ServerUrl.ToString().TrimEnd('/');
        ctx.EnvironmentVariables["NETCLAW_Mattermost__BotToken"] = result.Bot.Token;
        ctx.EnvironmentVariables["NETCLAW_Mattermost__DefaultChannelId"] = result.ChannelId;
        // CallbackUrl deliberately unset: the host-process daemon can't be
        // reached from the Mattermost container without poking holes in
        // ExposureMode.Local, so interactive button approvals fall back
        // to text-reply mode. Documented in the README.
        ctx.EnvironmentVariables["NETCLAW_Mattermost__MentionOnly"] = "false";
        ctx.EnvironmentVariables["NETCLAW_Mattermost__AllowDirectMessages"] = "true";
    })
    .WaitFor(mattermost);

builder.Build().Run();
