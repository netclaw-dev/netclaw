// -----------------------------------------------------------------------
// <copyright file="SkillCommandTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
    private const string PluginCommit = "13e26d39ed01d97ea592235d041304d289f4ba07";

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
        => SkillCommand.RunAsync(
            ["skill", "list"], _paths, TimeProvider.System, TextReader.Null, daemonApi, output: _output);

    private Task<int> RunSyncAsync(DaemonApi? daemonApi)
        => SkillCommand.RunAsync(
            ["skill", "sync"], _paths, TimeProvider.System, TextReader.Null, daemonApi, output: _output);

    private Task<int> RunRetrySyncAsync(DaemonApi? daemonApi)
        => SkillCommand.RunAsync(
            ["skill", "sync", "--retry-rejected"],
            _paths,
            TimeProvider.System,
            TextReader.Null,
            daemonApi,
            output: _output);

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
    public async Task Sync_retry_requests_an_explicit_rejected_commit_retry()
    {
        var daemonApi = CreateDaemonApi(request =>
        {
            Assert.Equal("?retryRejected=true", request.RequestUri!.Query);
            return FakeHttpMessageHandler.JsonResponse(new
            {
                passId = "pass-retry",
                sources = Array.Empty<object>(),
                inventory = new { succeeded = true, acceptedCount = 0, rejectedCount = 0 },
            });
        });

        var exit = await RunRetrySyncAsync(daemonApi);

        Assert.Equal(0, exit);
        Assert.Contains("pass-retry", _output.ToString());
    }

    [Fact]
    public async Task Plugin_install_waits_for_restart_and_immediate_sync()
    {
        var requests = new List<string>();
        var daemonApi = CreateDaemonApi(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            if (request.RequestUri.AbsolutePath == "/api/plugins"
                && request.Method == HttpMethod.Post)
            {
                return FakeHttpMessageHandler.JsonResponse(new
                {
                    restartGeneration = 4,
                    plugin = Plugin(ManagedPluginApi.PluginStatus.NotInstalled, installedCommit: null),
                });
            }
            if (request.RequestUri.AbsolutePath == "/api/health/ready")
            {
                var ready = new HttpResponseMessage(HttpStatusCode.OK);
                ready.Headers.Add("X-Netclaw-Generation", "5");
                return ready;
            }
            if (request.RequestUri.AbsolutePath == "/api/skills/sync")
            {
                return FakeHttpMessageHandler.JsonResponse(new
                {
                    passId = "install-pass",
                    sources = new[]
                    {
                        new
                        {
                            name = "fixture", sourceKind = SkillSyncResult.ServerFeedSourceKind,
                            changedCount = 0, unchangedCount = 0, rejectedCount = 0, failedCount = 1,
                            sidecar = "failed",
                        },
                        new
                        {
                            name = "fixture", sourceKind = SkillSyncResult.GitPluginSourceKind,
                            changedCount = 1, unchangedCount = 0, rejectedCount = 0, failedCount = 0,
                            sidecar = "not-applicable",
                        },
                    },
                    inventory = new { succeeded = true, acceptedCount = 1, rejectedCount = 0 },
                });
            }
            return FakeHttpMessageHandler.JsonResponse(new
            {
                plugins = new[]
                {
                    Plugin(ManagedPluginApi.PluginStatus.Installed, "13e26d39ed01d97ea592235d041304d289f4ba07"),
                },
            });
        });

        var exit = await PluginCommand.RunAsync(
            [
                "plugin", "install", "owner/repository", "--id", "fixture",
                "--commit", "13e26d39ed01d97ea592235d041304d289f4ba07",
                "--yes",
            ],
            daemonApi,
            TimeProvider.System,
            TextReader.Null,
            _output);

        Assert.True(exit == 0, _output.ToString());
        Assert.Equal(
            [
                "POST /api/plugins",
                "GET /api/health/ready",
                "POST /api/skills/sync",
                "GET /api/plugins",
            ],
            requests);
        Assert.Contains("Installed plugin 'fixture'", _output.ToString());
    }

    [Fact]
    public async Task Plugin_remove_requires_confirmation_before_the_daemon_request()
    {
        var requested = false;
        var daemonApi = CreateDaemonApi(_ =>
        {
            requested = true;
            return FakeHttpMessageHandler.JsonResponse(new { });
        });

        using var input = new StringReader("no\n");
        var exit = await PluginCommand.RunAsync(
            ["plugin", "remove", "fixture"],
            daemonApi,
            TimeProvider.System,
            input,
            _output);

        Assert.Equal(0, exit);
        Assert.False(requested);
        Assert.Contains("Cancelled.", _output.ToString());
    }

    [Fact]
    public async Task Plugin_enable_syncs_an_enabled_source_that_is_not_installed()
    {
        var requests = new List<string>();
        var listCount = 0;
        var daemonApi = CreateDaemonApi(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            if (request.Method == HttpMethod.Patch)
            {
                return FakeHttpMessageHandler.JsonResponse(new
                {
                    restartGeneration = 4,
                    sourceId = "fixture",
                    changed = false,
                });
            }
            if (request.RequestUri.AbsolutePath == "/api/plugins")
            {
                var status = listCount++ == 0
                    ? ManagedPluginApi.PluginStatus.NotInstalled
                    : ManagedPluginApi.PluginStatus.Installed;
                return FakeHttpMessageHandler.JsonResponse(new
                {
                    plugins = new[] { Plugin(status, status == ManagedPluginApi.PluginStatus.Installed
                        ? "13e26d39ed01d97ea592235d041304d289f4ba07"
                        : null) },
                });
            }
            return FakeHttpMessageHandler.JsonResponse(new
            {
                passId = "enable-pass",
                sources = new[]
                {
                    new
                    {
                        name = "fixture", sourceKind = SkillSyncResult.GitPluginSourceKind,
                        changedCount = 1, unchangedCount = 0, rejectedCount = 0, failedCount = 0,
                        sidecar = "not-applicable",
                    },
                },
                inventory = new { succeeded = true, acceptedCount = 1, rejectedCount = 0 },
            });
        });

        var exit = await PluginCommand.RunAsync(
            ["plugin", "enable", "fixture", "--yes"],
            daemonApi,
            TimeProvider.System,
            TextReader.Null,
            _output);

        Assert.True(exit == 0, _output.ToString());
        Assert.Equal(
            [
                "PATCH /api/plugins/fixture",
                "GET /api/plugins",
                "POST /api/skills/sync",
                "GET /api/plugins",
            ],
            requests);
    }

    [Fact]
    public async Task Plugin_disable_waits_when_an_equal_disk_change_is_not_live()
    {
        var requests = new List<string>();
        var listCount = 0;
        var daemonApi = CreateDaemonApi(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            if (request.Method == HttpMethod.Patch)
            {
                return FakeHttpMessageHandler.JsonResponse(new
                {
                    restartGeneration = 4,
                    sourceId = "fixture",
                    changed = false,
                });
            }
            if (request.RequestUri.AbsolutePath == "/api/health/ready")
            {
                var ready = new HttpResponseMessage(HttpStatusCode.OK);
                ready.Headers.Add("X-Netclaw-Generation", "5");
                return ready;
            }
            if (request.RequestUri.AbsolutePath == "/api/plugins")
            {
                var live = listCount++ > 0;
                return FakeHttpMessageHandler.JsonResponse(new
                {
                    plugins = new[]
                    {
                        live
                            ? Plugin(ManagedPluginApi.PluginStatus.Disabled, PluginCommit, enabled: false)
                            : Plugin(ManagedPluginApi.PluginStatus.Installed, PluginCommit, enabled: true),
                    },
                });
            }
            return FakeHttpMessageHandler.JsonResponse(new
            {
                passId = "disable-pass",
                sources = Array.Empty<object>(),
                inventory = new { succeeded = true, acceptedCount = 0, rejectedCount = 0 },
            });
        });

        var exit = await PluginCommand.RunAsync(
            ["plugin", "disable", "fixture", "--yes"],
            daemonApi,
            TimeProvider.System,
            TextReader.Null,
            _output);

        Assert.True(exit == 0, _output.ToString());
        Assert.Equal(
            [
                "PATCH /api/plugins/fixture",
                "GET /api/plugins",
                "GET /api/health/ready",
                "POST /api/skills/sync",
                "GET /api/plugins",
            ],
            requests);
    }

    [Fact]
    public async Task Plugin_usage_errors_return_exit_code_two_without_a_daemon_request()
    {
        var requested = false;
        var daemonApi = CreateDaemonApi(_ =>
        {
            requested = true;
            return FakeHttpMessageHandler.JsonResponse(new { });
        });

        var missingArgument = await PluginCommand.RunAsync(
            ["plugin", "install"], daemonApi, TimeProvider.System, TextReader.Null, _output);
        var unknownOption = await PluginCommand.RunAsync(
            ["plugin", "install", "owner/repository", "--unknown", "value"],
            daemonApi, TimeProvider.System, TextReader.Null, _output);
        var unknownAction = await PluginCommand.RunAsync(
            ["plugin", "unknown"], daemonApi, TimeProvider.System, TextReader.Null, _output);
        var unexpectedListArgument = await PluginCommand.RunAsync(
            ["plugin", "list", "extra"], daemonApi, TimeProvider.System, TextReader.Null, _output);

        Assert.Equal(2, missingArgument);
        Assert.Equal(2, unknownOption);
        Assert.Equal(2, unknownAction);
        Assert.Equal(2, unexpectedListArgument);
        Assert.False(requested);
    }

    [Fact]
    public async Task Plugin_source_validation_error_returns_exit_code_one()
    {
        var daemonApi = CreateDaemonApi(_ => throw new InvalidOperationException("No request expected."));

        var exit = await PluginCommand.RunAsync(
            ["plugin", "install", "https://example.test/repository", "--yes"],
            daemonApi, TimeProvider.System, TextReader.Null, _output);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task Plugin_update_source_validation_error_returns_exit_code_one()
    {
        var daemonApi = CreateDaemonApi(_ => throw new InvalidOperationException("No request expected."));

        var exit = await PluginCommand.RunAsync(
            ["plugin", "update", "Invalid_ID", "--yes"],
            daemonApi,
            TimeProvider.System,
            TextReader.Null,
            _output);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task Plugin_list_json_writes_one_stable_document()
    {
        var daemonApi = CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(new
        {
            plugins = new[]
            {
                Plugin(ManagedPluginApi.PluginStatus.Installed, PluginCommit),
            },
        }));

        var exit = await PluginCommand.RunAsync(
            ["plugin", "list", "--json"],
            daemonApi,
            TimeProvider.System,
            TextReader.Null,
            _output);

        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(_output.ToString());
        var plugin = Assert.Single(document.RootElement.GetProperty("plugins").EnumerateArray());
        Assert.Equal("fixture", plugin.GetProperty("sourceId").GetString());
        Assert.Equal("fixture-package", plugin.GetProperty("manifestName").GetString());
        Assert.DoesNotContain("NAME", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plugin_list_json_writes_an_empty_document_without_prose()
    {
        var daemonApi = CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(new
        {
            plugins = Array.Empty<object>(),
        }));

        var exit = await PluginCommand.RunAsync(
            ["plugin", "list", "--json"],
            daemonApi,
            TimeProvider.System,
            TextReader.Null,
            _output);

        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(_output.ToString());
        Assert.Empty(document.RootElement.GetProperty("plugins").EnumerateArray());
        Assert.DoesNotContain("No managed", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plugin_list_text_shows_source_and_manifest_names()
    {
        var daemonApi = CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(new
        {
            plugins = new[]
            {
                Plugin(ManagedPluginApi.PluginStatus.Installed, PluginCommit),
            },
        }));

        var exit = await PluginCommand.RunAsync(
            ["plugin", "list"],
            daemonApi,
            TimeProvider.System,
            TextReader.Null,
            _output);

        Assert.Equal(0, exit);
        Assert.Contains("SOURCE ID", _output.ToString());
        Assert.Contains("MANIFEST", _output.ToString());
        Assert.Contains("fixture-package", _output.ToString());
    }

    [Fact]
    public async Task Plugin_command_reports_a_safe_daemon_problem_detail()
    {
        var daemonApi = CreateDaemonApi(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"title\":\"Invalid plugin\",\"detail\":\"The source ID is invalid.\\nRetry.\"}",
                Encoding.UTF8,
                "application/problem+json"),
        });

        var exit = await PluginCommand.RunAsync(
            ["plugin", "list"],
            daemonApi,
            TimeProvider.System,
            TextReader.Null,
            _output);

        Assert.Equal(1, exit);
        Assert.Contains("The source ID is invalid. Retry.", _output.ToString());
        Assert.DoesNotContain("HTTP 400", _output.ToString());
    }

    [Fact]
    public async Task Plugin_command_replaces_an_oversized_error_body_with_a_status()
    {
        var remoteBody = new string('x', 5_000);
        var daemonApi = CreateDaemonApi(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(remoteBody, Encoding.UTF8, "application/problem+json"),
        });

        var exit = await PluginCommand.RunAsync(
            ["plugin", "list"],
            daemonApi,
            TimeProvider.System,
            TextReader.Null,
            _output);

        Assert.Equal(1, exit);
        Assert.Contains("HTTP 400", _output.ToString());
        Assert.DoesNotContain(new string('x', 100), _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plugin_update_uses_the_shared_sync_and_reports_one_source()
    {
        var requests = new List<string>();
        var daemonApi = CreateDaemonApi(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            if (request.Method == HttpMethod.Get)
            {
                return FakeHttpMessageHandler.JsonResponse(new
                {
                    plugins = new[]
                    {
                        Plugin(ManagedPluginApi.PluginStatus.Installed, PluginCommit),
                    },
                });
            }
            return FakeHttpMessageHandler.JsonResponse(new
            {
                passId = "update-pass",
                sources = new[]
                {
                    new
                    {
                        name = "fixture",
                        sourceKind = SkillSyncResult.GitPluginSourceKind,
                        changedCount = 1,
                        unchangedCount = 0,
                        rejectedCount = 0,
                        failedCount = 0,
                        sidecar = "not-applicable",
                        notices = new[] { "The importer ignored the unsupported MCP plugin component." },
                    },
                },
                inventory = new { succeeded = true, acceptedCount = 1, rejectedCount = 0 },
            });
        });

        var exit = await PluginCommand.RunAsync(
            ["plugin", "update", "fixture", "--retry-rejected", "--yes"],
            daemonApi,
            TimeProvider.System,
            TextReader.Null,
            _output);

        Assert.Equal(0, exit);
        Assert.Equal(
            ["GET /api/plugins", "POST /api/skills/sync?retryRejected=true"],
            requests);
        Assert.Contains("fixture: changed=1", _output.ToString());
        Assert.Contains("unsupported MCP", _output.ToString());
    }

    [Fact]
    public async Task Plugin_update_rejects_an_unknown_source_before_sync()
    {
        var requests = new List<string>();
        var daemonApi = CreateDaemonApi(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            return FakeHttpMessageHandler.JsonResponse(new { plugins = Array.Empty<object>() });
        });

        var exit = await PluginCommand.RunAsync(
            ["plugin", "update", "missing", "--yes"],
            daemonApi,
            TimeProvider.System,
            TextReader.Null,
            _output);

        Assert.Equal(1, exit);
        Assert.Equal(["GET /api/plugins"], requests);
        Assert.Contains("does not exist", _output.ToString());
    }

    [Fact]
    public async Task Plugin_help_lists_all_actions_without_a_daemon()
    {
        var exit = await PluginCommand.RunAsync(
            ["plugin", "--help"],
            daemonApi: null,
            TimeProvider.System,
            TextReader.Null,
            _output);

        Assert.Equal(0, exit);
        foreach (var action in new[] { "install", "list", "update", "enable", "disable", "remove" })
            Assert.Contains(action, _output.ToString());
    }

    [Fact]
    public async Task Skill_plugin_is_not_a_nested_command()
    {
        var exit = await SkillCommand.RunAsync(
            ["skill", "plugin", "list"],
            _paths,
            TimeProvider.System,
            TextReader.Null,
            daemonApi: null,
            output: _output);

        Assert.Equal(2, exit);
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
        var exit = await SkillCommand.RunAsync(
            ["skill", "list"],
            _paths,
            TimeProvider.System,
            TextReader.Null,
            daemonApi: null,
            output: _output);

        Assert.Equal(1, exit);
        Assert.Contains(UnavailableMarker, _output.ToString());
    }

    private static object Plugin(
        ManagedPluginApi.PluginStatus status,
        string? installedCommit,
        bool enabled = true) => new
        {
            sourceId = "fixture",
            manifestName = "fixture-package",
            repository = "owner/repository",
            sourceFormat = "codex",
            manifestFormat = "codex",
            subdirectory = (string?)null,
            referenceKind = ManagedPluginReferenceKind.Commit,
            reference = "13e26d39ed01d97ea592235d041304d289f4ba07",
            enabled,
            status,
            installedCommit,
            lastObservedCommit = installedCommit,
            installedVersion = "1.0.0",
        };
}
