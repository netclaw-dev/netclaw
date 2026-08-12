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
/// it never degrades to a disk scan that would drop the MCP prompts.
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

    private static DaemonApi CreateDaemonApi(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var configuration = new ConfigurationBuilder().Build();
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), $"netclaw-skill-api-{Guid.NewGuid():N}"));
        paths.EnsureDirectoriesExist();
        return new DaemonApi(new FakeHttpClientFactory(handler), configuration, paths);
    }

    private Task<int> RunListAsync(DaemonApi daemonApi)
        => SkillCommand.RunAsync(["skill", "list"], _paths, daemonApi, output: _output);

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
    public async Task List_reports_daemon_unavailable_when_unreachable()
    {
        var daemonApi = CreateDaemonApi(_ => throw new HttpRequestException("connection refused"));

        var exit = await RunListAsync(daemonApi);

        Assert.Equal(1, exit);
        Assert.Contains(UnavailableMarker, _output.ToString());
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
}
