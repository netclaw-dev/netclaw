// -----------------------------------------------------------------------
// <copyright file="McpProcessBoundStdioTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Text.Json;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

[Collection(McpSmokeChildProcessCollection.Name)]
public sealed class McpProcessBoundStdioTests
{
    [Fact]
    public async Task DifferentSessions_UseOneConfiguredProcess_WithoutArgumentRewriting()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var entry = CreateEntry("--netclaw-pass-through-probe");
        var registry = new ToolRegistry();
        await using var harness = McpSmokeHarness.Create(
            new Dictionary<string, McpServerEntry> { ["browser_playwright"] = entry }, registry);

        await harness.Manager.StartAsync(cts.Token);

        var first = await GetProcessInfoAsync(harness, "slack/channel/thread-a", cts.Token);
        var second = await GetProcessInfoAsync(harness, "slack/channel/thread-b", cts.Token);

        Assert.Equal(first.ProcessId, second.ProcessId);
        Assert.Contains("--netclaw-pass-through-probe", first.Arguments);
        Assert.DoesNotContain("--isolated", first.Arguments);

        using var process = Process.GetProcessById(first.ProcessId);
        await harness.Manager.StopAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task ExplicitIsolatedArgument_IsPreservedExactlyOnce()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var registry = new ToolRegistry();
        await using var harness = McpSmokeHarness.Create(
            new Dictionary<string, McpServerEntry>
            {
                ["browser_playwright"] = CreateEntry("--isolated"),
            },
            registry);

        await harness.Manager.StartAsync(cts.Token);

        var info = await GetProcessInfoAsync(harness, "slack/channel/thread", cts.Token);

        Assert.Single(info.Arguments, argument => argument == "--isolated");
    }

    private static McpServerEntry CreateEntry(params string[] extraArguments)
        => new()
        {
            Transport = "stdio",
            Command = "dotnet",
            Arguments = [SmokeMcpServerLocator.LocateDll(), .. extraArguments],
            Enabled = true,
        };

    private static async Task<ProcessInfo> GetProcessInfoAsync(
        McpSmokeHarness harness,
        string sessionId,
        CancellationToken ct)
    {
        var result = await harness.Manager.InvokeAsync(
            "browser_playwright",
            "process-info",
            null,
            TestToolExecutionContext.CreateBound(sessionId, null, TrustAudience.Personal).Invocation,
            ct);

        return JsonSerializer.Deserialize<ProcessInfo>(result, JsonOptions)!;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record ProcessInfo(int ProcessId, string[] Arguments);
}
