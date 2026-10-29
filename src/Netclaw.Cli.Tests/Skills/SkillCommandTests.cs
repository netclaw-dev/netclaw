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
        return new DaemonApi(new FakeHttpClientFactory(handler), configuration, paths, DaemonApiTestDependencies.CreateProofProtector(), TimeProvider.System);
    }

    private Task<int> RunListAsync(DaemonApi? daemonApi)
        => SkillCommand.RunAsync(["skill", "list"], _paths, daemonApi, output: _output);

    private Task<int> RunSyncAsync(DaemonApi? daemonApi)
        => SkillCommand.RunAsync(["skill", "sync"], _paths, daemonApi, output: _output);

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

    [Fact]
    public async Task Sync_posts_to_the_daemon_and_reports_a_successful_pass()
    {
        var daemonApi = CreateDaemonApi(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/skills/sync", request.RequestUri!.AbsolutePath);
            Assert.Contains("Waiting for the daemon's skill sync pass", _output.ToString());
            Assert.Contains("Ctrl+C", _output.ToString());
            return FakeHttpMessageHandler.JsonResponse(new
            {
                passId = "pass-1",
                sources = new[]
                {
                    new { name = "team", changedCount = 1, unchangedCount = 0, rejectedCount = 0, failedCount = 0, sidecar = "absent" },
                },
                inventory = new { succeeded = true, acceptedCount = 1, rejectedCount = 0 },
            });
        });

        var exit = await RunSyncAsync(daemonApi);

        Assert.Equal(0, exit);
        Assert.Contains("pass-1", _output.ToString());
        Assert.Contains("team: ok", _output.ToString());
    }

    [Fact]
    public async Task Sync_returns_nonzero_for_a_partial_source_failure()
    {
        var daemonApi = CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(new
        {
            passId = "pass-2",
            sources = new[]
            {
                new { name = "team", changedCount = 0, unchangedCount = 0, rejectedCount = 0, failedCount = 1, sidecar = "failed" },
            },
            inventory = new { succeeded = true, acceptedCount = 0, rejectedCount = 0 },
        }));

        var exit = await RunSyncAsync(daemonApi);

        Assert.Equal(1, exit);
        Assert.Contains("team: failed", _output.ToString());
    }

    [Fact]
    public async Task Sync_returns_nonzero_for_a_rejected_source_item()
    {
        var daemonApi = CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(new
        {
            passId = "pass-rejected",
            sources = new[]
            {
                new { name = "team", changedCount = 0, unchangedCount = 0, rejectedCount = 1, failedCount = 0, sidecar = "complete", error = "The scanner rejected a skill." },
            },
            inventory = new { succeeded = true, acceptedCount = 0, rejectedCount = 0 },
        }));

        var exit = await RunSyncAsync(daemonApi);

        Assert.Equal(1, exit);
        Assert.Contains("The scanner rejected a skill.", _output.ToString());
    }

    [Fact]
    public async Task Sync_returns_nonzero_for_a_malformed_result()
    {
        var daemonApi = CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(new
        {
            passId = "",
            sources = new[] { new { name = "", sidecar = "" } },
            inventory = new { succeeded = true },
        }));

        var exit = await RunSyncAsync(daemonApi);

        Assert.Equal(1, exit);
        Assert.Contains("unreadable result", _output.ToString());
    }

    [Fact]
    public async Task Sync_explains_a_version_skewed_daemon_on_404()
    {
        var daemonApi = CreateDaemonApi(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var exit = await RunSyncAsync(daemonApi);

        Assert.Equal(1, exit);
        Assert.Contains("Restart the daemon", _output.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "cannot run the pass now (HTTP 503)")]
    [InlineData(HttpStatusCode.InternalServerError, "returned HTTP 500")]
    [InlineData(HttpStatusCode.Forbidden, "returned HTTP 403")]
    public async Task Sync_distinguishes_an_http_failure_from_a_connection_failure(
        HttpStatusCode status, string expected)
    {
        var daemonApi = CreateDaemonApi(_ => new HttpResponseMessage(status));

        var exit = await RunSyncAsync(daemonApi);

        Assert.Equal(1, exit);
        Assert.Contains(expected, _output.ToString());
        Assert.DoesNotContain("could not reach", _output.ToString());
    }

    [Fact]
    public async Task Sync_returns_nonzero_when_only_the_inventory_refresh_fails()
    {
        var daemonApi = CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(new
        {
            passId = "pass-inventory-failure",
            sources = Array.Empty<object>(),
            inventory = new { succeeded = false, error = "The skill inventory refresh failed." },
        }));

        var exit = await RunSyncAsync(daemonApi);

        Assert.Equal(1, exit);
        Assert.Contains("Inventory: failed", _output.ToString());
        Assert.Contains("The skill inventory refresh failed.", _output.ToString());
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

    // ── show ──────────────────────────────────────────────────────────

    private Task<int> RunShowAsync(DaemonApi? daemonApi, string name)
        => SkillCommand.RunAsync(["skill", "show", name], _paths, daemonApi, output: _output);

    private static object ServerFedSkillDetail(string name, string version, string content, string feedDirectory)
        => new
        {
            skill = new
            {
                name,
                displayName = name,
                description = "A server-fed skill.",
                source = "server-feed",
                category = "server-feed",
                version,
                userInvocable = true,
                modelInvocable = true,
            },
            filePath = Path.Combine(feedDirectory, name, "SKILL.md"),
            skillDirectory = Path.Combine(feedDirectory, name),
            isFlatFile = false,
            content,
            allowedTools = (string?)null,
            resources = (object?)null,
        };

    [Fact]
    public async Task Show_renders_a_server_fed_skill_from_the_daemon()
    {
        // A server-fed skill lives three levels below the skills root, so no offline
        // scan can see it. The daemon is the only complete source.
        var daemonApi = CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(
            ServerFedSkillDetail("git", "1.3.1", "---\nname: git\n---\n# Git Hygiene\n", "/feed")));

        var exit = await RunShowAsync(daemonApi, "git");

        var text = _output.ToString();
        Assert.Equal(0, exit);
        Assert.Contains("Name:        git", text);
        Assert.Contains("Source:      server-feed", text);
        Assert.Contains("Version:     1.3.1", text);
        Assert.Contains("# Git Hygiene", text);
    }

    [Fact]
    public async Task Show_queries_the_daemon_detail_route_for_the_requested_name()
    {
        Uri? requested = null;
        var daemonApi = CreateDaemonApi(request =>
        {
            requested = request.RequestUri;
            return FakeHttpMessageHandler.JsonResponse(
                ServerFedSkillDetail("git", "1.3.1", "body", "/feed"));
        });

        await RunShowAsync(daemonApi, "git");

        Assert.NotNull(requested);
        Assert.Equal("/api/skills/git", requested!.AbsolutePath);
    }

    [Theory]
    [InlineData("show")]
    [InlineData("remove")]
    public async Task Detail_reports_a_name_the_daemon_does_not_know(string operation)
    {
        // The route answers an unknown name with a problem body; the CLI must read that
        // as "not found", not as a missing route.
        var daemonApi = CreateDaemonApi(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                """{"title":"Skill not found","status":404}""",
                Encoding.UTF8,
                "application/problem+json"),
        });

        var exit = await SkillCommand.RunAsync(["skill", operation, "nope"], _paths, daemonApi, _output);

        Assert.Equal(1, exit);
        // "[FAIL] skill not found" goes to stderr, which this harness does not capture.
        // What matters is the distinction: this is NOT reported as a version mismatch,
        // and stdout stays clean so scripts can parse it.
        Assert.DoesNotContain("does not serve", _output.ToString());
        Assert.Equal(string.Empty, _output.ToString());
    }

    [Theory]
    [InlineData("show")]
    [InlineData("remove")]
    public async Task Detail_reports_a_daemon_that_predates_the_detail_route(string operation)
    {
        // A bare 404 with no body means the route does not exist on this daemon. That is
        // a version mismatch, not a missing skill, and must say so.
        var daemonApi = CreateDaemonApi(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var exit = await SkillCommand.RunAsync(["skill", operation, "git"], _paths, daemonApi, _output);

        Assert.Equal(1, exit);
        var text = _output.ToString();
        Assert.Contains("does not serve", text);
        Assert.Contains("Restart the daemon", text);
        Assert.DoesNotContain("could not reach", text);
        Assert.DoesNotContain("'git' not found", text);
    }

    [Theory]
    [InlineData("show")]
    [InlineData("remove")]
    public async Task Detail_reports_daemon_unavailable_without_falling_back_to_disk(string operation)
    {
        var daemonApi = CreateDaemonApi(_ => throw new HttpRequestException("connection refused"));

        var exit = await SkillCommand.RunAsync(["skill", operation, "git"], _paths, daemonApi, _output);

        Assert.Equal(1, exit);
        Assert.Contains("unavailable", _output.ToString());
    }

    [Theory]
    [InlineData("show", true)]
    [InlineData("show", false)]
    [InlineData("remove", true)]
    [InlineData("remove", false)]
    public async Task Detail_reports_a_timeout_or_invalid_json_and_preserves_local_files(string operation, bool timeout)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_paths.SkillsDirectory, "git")).FullName;
        var file = Path.Combine(directory, "SKILL.md");
        File.WriteAllText(file, "# Sentinel");
        var daemonApi = CreateDaemonApi(_ => timeout
            ? throw new OperationCanceledException()
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{") });

        var exit = await SkillCommand.RunAsync(["skill", operation, "git"], _paths, daemonApi, _output);

        Assert.Equal(1, exit);
        Assert.Contains(timeout ? "timed out" : "unreadable skill", _output.ToString());
        Assert.Equal("# Sentinel", File.ReadAllText(file));
    }

    // ── remove ────────────────────────────────────────────────────────

    private Task<int> RunRemoveAsync(DaemonApi? daemonApi, string name)
        => SkillCommand.RunAsync(["skill", "remove", name], _paths, daemonApi, output: _output);

    [Fact]
    public async Task Remove_refuses_a_server_fed_skill_and_leaves_it_on_disk()
    {
        // Feed directories are daemon-managed. Deleting one would look like a pruning
        // event to the sync actor, so remove must refuse.
        var feedDirectory = Path.Combine(_paths.ServerFeedsDirectory, "team");
        var skillDirectory = Path.Combine(feedDirectory, "git");
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), "---\nname: git\n---\n# Git\n");

        var daemonApi = CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(
            ServerFedSkillDetail("git", "1.3.1", "body", feedDirectory)));

        var exit = await RunRemoveAsync(daemonApi, "git");

        Assert.Equal(1, exit);
        Assert.Contains("server-fed", _output.ToString());
        Assert.True(Directory.Exists(skillDirectory));
        Assert.True(Directory.Exists(feedDirectory));
    }

    [Fact]
    public async Task Remove_refuses_a_native_skill_whose_path_is_outside_the_skills_root()
    {
        // A daemon response is remote input. If it points outside the skills root, or is
        // stale or compromised, `remove` must not recursively delete that directory.
        var outside = Path.Combine(_dir.Path, "outside-root");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "SKILL.md"), "---\nname: evil\n---\n# Evil\n");

        var daemonApi = CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(new
        {
            skill = new
            {
                name = "evil",
                displayName = "evil",
                description = "A skill outside the root.",
                source = "native",
                category = (string?)null,
                version = (string?)null,
                userInvocable = true,
                modelInvocable = true,
            },
            filePath = Path.Combine(outside, "SKILL.md"),
            skillDirectory = outside,
            isFlatFile = false,
            content = "# Evil",
            allowedTools = (string?)null,
            resources = (object?)null,
        }));

        var exit = await RunRemoveAsync(daemonApi, "evil");

        Assert.Equal(1, exit);
        Assert.Contains("outside the skills root", _output.ToString());
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public async Task Remove_refuses_a_sibling_directory_that_shares_the_skills_root_prefix()
    {
        // "/tmp/x/skills-evil" must not be treated as inside "/tmp/x/skills".
        var sibling = Path.GetFullPath(_paths.SkillsDirectory) + "-evil";
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "SKILL.md"), "---\nname: sibling\n---\n# Sibling\n");

        var daemonApi = CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(new
        {
            skill = new
            {
                name = "sibling",
                displayName = "sibling",
                description = "A prefix-sharing sibling.",
                source = "native",
                category = (string?)null,
                version = (string?)null,
                userInvocable = true,
                modelInvocable = true,
            },
            filePath = Path.Combine(sibling, "SKILL.md"),
            skillDirectory = sibling,
            isFlatFile = false,
            content = "# Sibling",
            allowedTools = (string?)null,
            resources = (object?)null,
        }));

        try
        {
            var exit = await RunRemoveAsync(daemonApi, "sibling");

            Assert.Equal(1, exit);
            Assert.True(Directory.Exists(sibling));
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    [Fact]
    public async Task Remove_deletes_a_native_flat_skill_file()
    {
        // A flat skill's SkillDirectory is the scan root, so the root guard must not
        // block removing the file itself.
        var flatFile = Path.Combine(_paths.SkillsDirectory, "flat-skill.md");
        File.WriteAllText(flatFile, "---\nname: flat-skill\ndescription: A flat skill.\n---\n\n# Flat\n");

        var daemonApi = CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(new
        {
            skill = new
            {
                name = "flat-skill",
                displayName = "flat-skill",
                description = "A flat skill.",
                source = "native",
                category = (string?)null,
                version = (string?)null,
                userInvocable = true,
                modelInvocable = true,
            },
            filePath = flatFile,
            skillDirectory = _paths.SkillsDirectory,
            isFlatFile = true,
            content = "# Flat",
            allowedTools = (string?)null,
            resources = (object?)null,
        }));

        var exit = await RunRemoveAsync(daemonApi, "flat-skill");

        Assert.Equal(0, exit);
        Assert.False(File.Exists(flatFile));
        Assert.True(Directory.Exists(_paths.SkillsDirectory));
    }

    [Fact]
    public async Task Remove_deletes_a_native_skill_directory()
    {
        var skillDirectory = Path.Combine(_paths.SkillsDirectory, "local-skill");
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), "---\nname: local-skill\n---\n# Local\n");

        var daemonApi = CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(new
        {
            skill = new
            {
                name = "local-skill",
                displayName = "local-skill",
                description = "A native skill.",
                source = "native",
                category = (string?)null,
                version = (string?)null,
                userInvocable = true,
                modelInvocable = true,
            },
            filePath = Path.Combine(skillDirectory, "SKILL.md"),
            skillDirectory,
            content = "# Local",
            allowedTools = (string?)null,
            resources = (object?)null,
        }));

        var exit = await RunRemoveAsync(daemonApi, "local-skill");

        Assert.Equal(0, exit);
        Assert.False(Directory.Exists(skillDirectory));
    }
}
