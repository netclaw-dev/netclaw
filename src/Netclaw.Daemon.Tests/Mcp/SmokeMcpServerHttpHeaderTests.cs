// -----------------------------------------------------------------------
// <copyright file="SmokeMcpServerHttpHeaderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Text.RegularExpressions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

/// <summary>
/// End-to-end regression for `netclaw mcp add --header "Authorization: …"`
/// against an HTTP MCP server. Spawns the smoke server in `--transport http
/// --capture-auth` mode, points an <see cref="Daemon.Mcp.McpClientManager"/>
/// at it with a configured Authorization header, and verifies via the
/// server's `last_auth_header` tool that the header arrived intact.
///
/// This pins the bug fixed alongside it: <c>McpServerEntry.Headers</c> being
/// typed <c>Dictionary&lt;string, string&gt;</c> meant the daemon-side
/// <see cref="SensitiveStringTypeConverter"/> never decrypted <c>ENC:</c>
/// ciphertext from <c>secrets.json</c>, so HTTP MCP servers that authenticate
/// on the first byte (e.g. mcp.atlassian.com) received a garbage Authorization
/// value and returned 401. The exact same data path is exercised here: a
/// configured header → <c>HttpClientTransport.AdditionalHeaders</c> → wire →
/// server-side capture.
/// </summary>
public sealed class SmokeMcpServerHttpHeaderTests
{
    [Fact]
    public async Task ConfiguredHeader_IsAttachedToOutboundMcpRequest()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        const string expectedHeader = "Bearer test-creds-from-header-flag";

        await using var server = await SmokeHttpMcpServer.StartAsync(ct);

        var entry = new McpServerEntry
        {
            Transport = "http",
            Url = server.Url,
            Enabled = true,
            Headers = new Dictionary<string, SensitiveString>
            {
                ["Authorization"] = new SensitiveString(expectedHeader),
            },
        };

        var registry = new ToolRegistry();
        await using var harness = McpSmokeHarness.Create(
            new Dictionary<string, McpServerEntry> { ["smoke-http"] = entry }, registry);

        await harness.Manager.StartAsync(ct);

        var lastAuthHeader = registry.GetAllRegistrations()
            .Select(r => r.Tool)
            .OfType<McpToolAdapter>()
            .SingleOrDefault(t => t.Name == "smoke-http/last_auth_header");
        Assert.NotNull(lastAuthHeader);

        var observed = await lastAuthHeader!.ExecuteAsync(
            new Dictionary<string, object?>(), TestToolExecutionContext.CreateUnbound(), ct);

        Assert.Contains(expectedHeader, observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Netclaw_user_agent_and_component_headers_are_attached_to_mcp_requests()
    {
        // Identity smoke test: every outbound MCP HTTP request must advertise
        // a Netclaw/-prefixed User-Agent and the X-Netclaw-Component=mcp marker
        // so server operators (e.g. TextForge) can see who is calling.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var server = await SmokeHttpMcpServer.StartAsync(ct);

        var entry = new McpServerEntry
        {
            Transport = "http",
            Url = server.Url,
            Enabled = true,
        };

        var registry = new ToolRegistry();
        await using var harness = McpSmokeHarness.Create(
            new Dictionary<string, McpServerEntry> { ["smoke-http"] = entry }, registry);

        await harness.Manager.StartAsync(ct);

        var lastUserAgent = registry.GetAllRegistrations()
            .Select(r => r.Tool)
            .OfType<McpToolAdapter>()
            .SingleOrDefault(t => t.Name == "smoke-http/last_user_agent");
        Assert.NotNull(lastUserAgent);
        var observedUa = await lastUserAgent!.ExecuteAsync(
            new Dictionary<string, object?>(), TestToolExecutionContext.CreateUnbound(), ct);
        // Pin the exact UA we expect on the wire so a regression that ships
        // "Netclaw/0.0.0 (...; sha=unknown)" against testhost still fails.
        Assert.Contains(NetclawUserAgent.Value, observedUa, StringComparison.Ordinal);

        var lastComponent = registry.GetAllRegistrations()
            .Select(r => r.Tool)
            .OfType<McpToolAdapter>()
            .SingleOrDefault(t => t.Name == "smoke-http/last_netclaw_component");
        Assert.NotNull(lastComponent);
        var observedComponent = await lastComponent!.ExecuteAsync(
            new Dictionary<string, object?>(), TestToolExecutionContext.CreateUnbound(), ct);
        // Daemon path advertises "mcp" exactly; the CLI probe path uses
        // "mcp-probe". Substring "mcp" alone would not catch a swap.
        Assert.Contains("mcp", observedComponent, StringComparison.Ordinal);
        Assert.DoesNotContain("probe", observedComponent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoConfiguredHeader_ResultsInNoAuthorizationHeaderOnTheWire()
    {
        // Counterpart to the test above: if the operator did not configure
        // an Authorization header, the SDK MUST NOT invent one. The smoke
        // server returns "(none)" when it sees no Authorization header.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var server = await SmokeHttpMcpServer.StartAsync(ct);

        var entry = new McpServerEntry
        {
            Transport = "http",
            Url = server.Url,
            Enabled = true,
        };

        var registry = new ToolRegistry();
        await using var harness = McpSmokeHarness.Create(
            new Dictionary<string, McpServerEntry> { ["smoke-http"] = entry }, registry);

        await harness.Manager.StartAsync(ct);

        var lastAuthHeader = registry.GetAllRegistrations()
            .Select(r => r.Tool)
            .OfType<McpToolAdapter>()
            .SingleOrDefault(t => t.Name == "smoke-http/last_auth_header");
        Assert.NotNull(lastAuthHeader);

        var observed = await lastAuthHeader!.ExecuteAsync(
            new Dictionary<string, object?>(), TestToolExecutionContext.CreateUnbound(), ct);

        Assert.Contains("(none)", observed, StringComparison.Ordinal);
    }

    /// <summary>
    /// End-to-end regression for #1350: the smoke server returns 401 +
    /// WWW-Authenticate with OAuth resource_metadata on unauthenticated
    /// requests, but the user has a static Authorization header configured.
    /// Both the OAuth probe and the MCP transport hit the same real server —
    /// no fakes. The probe caches metadata but must NOT block the connection;
    /// the static header must reach the server and the connection must succeed.
    /// </summary>
    [Fact]
    public async Task ConfiguredHeader_WhenOAuthProbeReturnsMetadata_StillReachesServer()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        const string expectedHeader = "Bearer static-token-oauth-probe-test";

        await using var server = await SmokeHttpMcpServer.StartAsync(ct, requireAuth: true);

        var entry = new McpServerEntry
        {
            Transport = "http",
            Url = server.Url,
            Enabled = true,
            Headers = new Dictionary<string, SensitiveString>
            {
                ["Authorization"] = new SensitiveString(expectedHeader),
            },
        };

        var registry = new ToolRegistry();
        await using var harness = McpSmokeHarness.Create(
            new Dictionary<string, McpServerEntry> { ["smoke-http"] = entry }, registry);

        await harness.Manager.StartAsync(ct);

        var lastAuthHeader = registry.GetAllRegistrations()
            .Select(r => r.Tool)
            .OfType<McpToolAdapter>()
            .SingleOrDefault(t => t.Name == "smoke-http/last_auth_header");
        Assert.NotNull(lastAuthHeader);

        var observed = await lastAuthHeader!.ExecuteAsync(
            new Dictionary<string, object?>(), TestToolExecutionContext.CreateUnbound(), ct);

        Assert.Contains(expectedHeader, observed, StringComparison.Ordinal);
    }
}

/// <summary>
/// Owns the smoke MCP server child process for the duration of a test.
/// Spawns <c>Netclaw.SmokeMcpServer.dll</c> in HTTP mode with
/// <c>--port 0</c>, reads the chosen ephemeral port from stderr, and exposes
/// the resulting <see cref="Url"/>. On <see cref="DisposeAsync"/> the
/// process is killed and drained so test runs cannot leak orphaned servers.
/// </summary>
internal sealed class SmokeHttpMcpServer : IAsyncDisposable
{
    private static readonly Regex ListeningPattern = new(
        @"\[smoke-mcp:listening\]\s+(?<url>https?://[^\s]+)",
        RegexOptions.Compiled);

    private readonly Process _process;
    private readonly Task _stderrDrain;
    private readonly Task _stdoutDrain;

    private SmokeHttpMcpServer(Process process, string url, Task stderrDrain, Task stdoutDrain)
    {
        _process = process;
        _stderrDrain = stderrDrain;
        _stdoutDrain = stdoutDrain;
        Url = url;
    }

    public string Url { get; }

    public static async Task<SmokeHttpMcpServer> StartAsync(
        CancellationToken ct, bool requireAuth = false)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add(SmokeMcpServerLocator.LocateDll());
        info.ArgumentList.Add("--transport");
        info.ArgumentList.Add("http");
        info.ArgumentList.Add("--port");
        info.ArgumentList.Add("0");
        info.ArgumentList.Add("--capture-auth");
        if (requireAuth)
            info.ArgumentList.Add("--require-auth");

        var process = Process.Start(info)
            ?? throw new InvalidOperationException("Failed to start smoke MCP server");

        var listeningTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Drain stderr to completion: scan for the listening line, then keep
        // reading so a full pipe doesn't deadlock the child.
        var stderrDrain = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                var match = ListeningPattern.Match(line);
                if (match.Success && !listeningTcs.Task.IsCompleted)
                    listeningTcs.TrySetResult(match.Groups["url"].Value);
            }
            listeningTcs.TrySetException(new InvalidOperationException(
                "Smoke MCP server exited before publishing a listening URL"));
        }, ct);

        // Drain stdout too — Kestrel may log there and a full pipe will
        // wedge the child. Tracked symmetrically with stderr so DisposeAsync
        // can await it.
        var stdoutDrain = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is not null) { }
            }
            // slopwatch-ignore: SW003 stdout drain ends with the test cancellation token; this is the expected teardown exit, not a swallowed error.
            catch (OperationCanceledException) { }
            // slopwatch-ignore: SW003 the child's stdout stream closes when DisposeAsync kills the process; expected teardown race, not a swallowed error.
            catch (IOException) { }
        }, ct);

        // Bounded wait for the child to publish its listening URL on stderr;
        // listeningTcs is the actual sync primitive — the timeout is just a
        // fail-fast for "child never started" rather than a flake-buffer.
        string url;
        try
        {
            url = await listeningTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            TryKill(process);
            throw new TimeoutException("Smoke MCP server did not publish a listening URL within 30s");
        }

        return new SmokeHttpMcpServer(process, url, stderrDrain, stdoutDrain);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
            TryKill(_process);

        await _process.WaitForExitAsync().ConfigureAwait(false);

        // Drains terminate once the streams close; await both so teardown
        // doesn't leave Task.Run continuations executing past the test. The
        // drains' own catches handle the cancellation/IO race; surfacing
        // anything else here would mask a real teardown bug.
        await IgnoreCancellationAsync(_stderrDrain).ConfigureAwait(false);
        await IgnoreCancellationAsync(_stdoutDrain).ConfigureAwait(false);

        _process.Dispose();
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        // slopwatch-ignore: SW003 race between HasExited check and Kill — the child exited on its own, nothing to do.
        catch (InvalidOperationException) { }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        // slopwatch-ignore: SW003 drains are tied to the test cancellation token; cancellation on teardown is the expected exit, not a failure to surface.
        catch (OperationCanceledException) { }
        // slopwatch-ignore: SW003 stderr drain TCS surfaces this when the child exits before publishing a listening URL; the caller already saw the TimeoutException StartAsync threw.
        catch (InvalidOperationException) { }
    }
}
