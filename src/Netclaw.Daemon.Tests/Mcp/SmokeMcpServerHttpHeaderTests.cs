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
            new Dictionary<string, object?>(), ToolExecutionContext.Empty, ct);

        Assert.Contains(expectedHeader, observed, StringComparison.Ordinal);
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
            new Dictionary<string, object?>(), ToolExecutionContext.Empty, ct);

        Assert.Contains("(none)", observed, StringComparison.Ordinal);
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

    public static async Task<SmokeHttpMcpServer> StartAsync(CancellationToken ct)
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
            catch { /* drain best-effort */ }
        }, ct);

        var startTimeout = Task.Delay(TimeSpan.FromSeconds(30), ct);
        var completed = await Task.WhenAny(listeningTcs.Task, startTimeout).ConfigureAwait(false);
        if (completed != listeningTcs.Task)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw new TimeoutException("Smoke MCP server did not start within 30s");
        }

        return new SmokeHttpMcpServer(
            process, await listeningTcs.Task.ConfigureAwait(false), stderrDrain, stdoutDrain);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); }
            catch { /* best-effort */ }
        }

        try { await _process.WaitForExitAsync().ConfigureAwait(false); }
        catch { /* best-effort */ }

        // Drains terminate when the streams close; await both so teardown
        // doesn't leave Task.Run continuations executing past the test.
        try { await _stderrDrain.ConfigureAwait(false); }
        catch { /* drain is best-effort once we're tearing down */ }

        try { await _stdoutDrain.ConfigureAwait(false); }
        catch { /* drain is best-effort once we're tearing down */ }

        _process.Dispose();
    }
}
