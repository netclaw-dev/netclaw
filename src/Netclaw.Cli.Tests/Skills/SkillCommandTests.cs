// -----------------------------------------------------------------------
// <copyright file="SkillCommandTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Skills;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Skills;

/// <summary>
/// Tests that <c>netclaw skill list</c> is served by the daemon (so it surfaces
/// dynamic MCP prompt skills) and, per the no-silent-fallbacks rule, reports the
/// daemon as unavailable and exits non-zero when it cannot get a usable response —
/// it never degrades to a disk scan that would drop the MCP prompts, and it never
/// surfaces a stack trace for an unavailable or misconfigured daemon.
/// </summary>
public sealed class SkillCommandTests : IDisposable
{
    private const string UnavailableMarker = "Daemon unavailable";

    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly StringWriter _output = new();

    public SkillCommandTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        _output.Dispose();
        _dir.Dispose();
    }

    private DaemonApi CreateDaemonApi(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var configuration = new ConfigurationBuilder().Build();
        // Nested under the test's own temp dir so Dispose cleans it up.
        var paths = new NetclawPaths(Path.Combine(_dir.Path, $"api-{Guid.NewGuid():N}"));
        paths.EnsureDirectoriesExist();
        return new DaemonApi(new FakeHttpClientFactory(handler), configuration, paths);
    }

    private Task<int> RunListAsync(DaemonApi? daemonApi)
        => SkillCommand.RunAsync(["skill", "list"], _paths, daemonApi, output: _output);

    // ── Success paths ─────────────────────────────────────────────────

    [Fact]
    public async Task List_renders_dynamic_mcp_prompt_skills_from_the_daemon()
    {
        var daemonApi = CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(new
        {
            skills = new object[]
            {
                new
                {
                    name = "mcp__demo__hello",
                    displayName = "hello",
                    description = "A demo MCP prompt.",
                    source = "mcp",
                    serverName = "demo",
                    promptName = "hello",
                    version = (string?)null,
                    category = "mcp",
                    userInvocable = false,
                    modelInvocable = true,
                },
                new
                {
                    name = "commit",
                    displayName = "commit",
                    description = "A file skill.",
                    source = "native",
                    version = "1.0.0",
                    category = (string?)null,
                    userInvocable = true,
                    modelInvocable = true,
                },
            },
        }));

        var exit = await RunListAsync(daemonApi);

        Assert.Equal(0, exit);
        var text = _output.ToString();
        Assert.Contains("mcp__demo__hello", text);
        // "native" is not a substring of any skill name, so seeing it proves the
        // SOURCE column actually rendered (not just the name).
        Assert.Contains("native", text);
        Assert.DoesNotContain(UnavailableMarker, text);
    }

    [Fact]
    public async Task List_renders_no_skills_found_for_an_empty_inventory()
    {
        // An empty registry is a REAL answer from a healthy daemon — exit 0,
        // never "Daemon unavailable".
        var daemonApi = CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(new
        {
            skills = Array.Empty<object>(),
        }));

        var exit = await RunListAsync(daemonApi);

        Assert.Equal(0, exit);
        var text = _output.ToString();
        Assert.Contains("No skills found", text);
        Assert.DoesNotContain(UnavailableMarker, text);
    }

    // ── Daemon-unavailable paths: report + exit 1, never a stack trace ──

    [Fact]
    public async Task List_reports_daemon_unavailable_when_unreachable()
    {
        var daemonApi = CreateDaemonApi(_ => throw new HttpRequestException("connection refused"));

        var exit = await RunListAsync(daemonApi);

        Assert.Equal(1, exit);
        Assert.Contains(UnavailableMarker, _output.ToString());
    }

    [Fact]
    public async Task List_reports_daemon_unavailable_on_a_server_error_status()
    {
        var daemonApi = CreateDaemonApi(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var exit = await RunListAsync(daemonApi);

        Assert.Equal(1, exit);
        Assert.Contains(UnavailableMarker, _output.ToString());
    }

    [Fact]
    public async Task List_explains_a_version_skewed_daemon_on_404()
    {
        // An updated CLI against a still-running older daemon: the route does not
        // exist yet. "Start the daemon" would mislead — it must say restart instead.
        var daemonApi = CreateDaemonApi(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var exit = await RunListAsync(daemonApi);

        Assert.Equal(1, exit);
        var text = _output.ToString();
        Assert.Contains(UnavailableMarker, text);
        Assert.Contains("Restart the daemon", text);
        Assert.DoesNotContain("netclaw daemon start", text);
    }

    [Fact]
    public async Task List_reports_daemon_unavailable_on_a_non_json_body()
    {
        // A foreign listener / captive portal / reverse proxy answering 200 with HTML
        // makes JsonSerializer throw — the CLI must report unavailable, not crash.
        var daemonApi = CreateDaemonApi(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>not a skill list</html>", Encoding.UTF8, "text/html"),
        });

        var exit = await RunListAsync(daemonApi);

        Assert.Equal(1, exit);
        Assert.Contains(UnavailableMarker, _output.ToString());
    }

    [Fact]
    public async Task List_reports_daemon_unavailable_on_null_skills()
    {
        // {"skills":null} satisfies `required` by presence but leaves Skills null —
        // the CLI must guard against it, not NRE.
        var daemonApi = CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(new { skills = (object?)null }));

        var exit = await RunListAsync(daemonApi);

        Assert.Equal(1, exit);
        Assert.Contains(UnavailableMarker, _output.ToString());
    }

    [Fact]
    public async Task List_reports_daemon_unavailable_when_the_request_fails_before_http()
    {
        // A malformed endpoint string (e.g. NETCLAW_DAEMON_ENDPOINT=localhost:5199,
        // no scheme) throws NotSupportedException/UriFormatException before any HTTP
        // happens; a corrupt device token throws CryptographicException. All must
        // land in the trailing catch as "Daemon unavailable", not a core dump.
        var daemonApi = CreateDaemonApi(_ => throw new NotSupportedException("The 'localhost' scheme is not supported."));

        var exit = await RunListAsync(daemonApi);

        Assert.Equal(1, exit);
        Assert.Contains(UnavailableMarker, _output.ToString());
    }

    [Fact]
    public async Task List_reports_daemon_unavailable_when_no_daemon_api_is_supplied()
    {
        var exit = await SkillCommand.RunAsync(["skill", "list"], _paths, daemonApi: null, output: _output);

        Assert.Equal(1, exit);
        Assert.Contains(UnavailableMarker, _output.ToString());
    }
}
