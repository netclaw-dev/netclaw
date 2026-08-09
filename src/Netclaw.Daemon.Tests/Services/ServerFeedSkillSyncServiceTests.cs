// -----------------------------------------------------------------------
// <copyright file="ServerFeedSkillSyncServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Skills;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;
using Netclaw.Daemon.Services;
using Netclaw.Security.Skills;
using Netclaw.SkillClient;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class ServerFeedSkillSyncServiceTests : IDisposable
{
    private const string BaseUrl = "https://skillserver.test/";

    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly SkillRegistry _skillRegistry = new();
    private readonly SkillIndexContextLayer _skillIndexLayer = new();
    private readonly SkillIndexPublisher _skillIndexPublisher;

    public ServerFeedSkillSyncServiceTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        _skillIndexPublisher = new SkillIndexPublisher(
            _skillRegistry,
            _skillIndexLayer,
            static (_, _) => true);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task ExtractArchiveAsync_AllowsArbitraryResourcesAndPreservesExecutableMode()
    {
        var skillContent = Encoding.UTF8.GetBytes("""
            ---
            name: packaged
            description: Packaged skill.
            ---

            # Packaged
            """);
        var scriptContent = Encoding.UTF8.GetBytes("#!/bin/bash\necho ok\n");
        var binaryContent = new byte[] { 0x00, 0x01, 0xFF, 0x02 };
        var archive = BuildArchive(
            ("SKILL.md", skillContent, 0x1A4),
            ("tools/check", scriptContent, 0x1ED),
            ("assets/icon.bin", binaryContent, 0x1A4));

        var files = await CreateService().ExtractArchiveAsync(
            "packaged", "private", archive, TestContext.Current.CancellationToken);

        Assert.NotNull(files);
        Assert.Contains(files!, file => file.RelativePath == "tools/check" && file.UnixMode == 0x1ED);
        Assert.Contains(files, file => file.RelativePath == "assets/icon.bin");

        var feedDir = _paths.ServerFeedDirectory("private");
        await SkillSyncHelpers.ReplaceSkillDirectoryAsync(
            feedDir, "packaged", files!, TestContext.Current.CancellationToken);

        var skillDir = Path.Combine(feedDir, "packaged");
        Assert.Equal(scriptContent, await File.ReadAllBytesAsync(Path.Combine(skillDir, "tools", "check"), TestContext.Current.CancellationToken));
        Assert.Equal(binaryContent, await File.ReadAllBytesAsync(Path.Combine(skillDir, "assets", "icon.bin"), TestContext.Current.CancellationToken));

        if (!OperatingSystem.IsWindows())
        {
            var mode = (int)File.GetUnixFileMode(Path.Combine(skillDir, "tools", "check")) & 0x1FF;
            Assert.Equal(0x1ED, mode);
        }
    }

    [Fact]
    public async Task ExtractArchiveAsync_RejectsTraversalEntries()
    {
        var skillContent = Encoding.UTF8.GetBytes("""
            ---
            name: packaged
            description: Packaged skill.
            ---

            # Packaged
            """);
        var archive = BuildArchive(
            ("SKILL.md", skillContent, 0x1A4),
            ("../escape.sh", Encoding.UTF8.GetBytes("echo no"), 0x1ED));

        var files = await CreateService().ExtractArchiveAsync(
            "packaged", "private", archive, TestContext.Current.CancellationToken);

        Assert.Null(files);
    }

    [Fact]
    public async Task SyncOnce_syncs_native_subagent_from_sidecar_after_empty_rfc_index()
    {
        var agentContent = AgentMarkdown("code-reviewer", "Managed reviewer", "Review code carefully. 请仔细审查代码。");
        var digest = SkillSyncHelpers.ComputeSha256(agentContent);

        var handler = new FakeHttpMessageHandler();
        AddEmptyRfcIndex(handler);
        AddNativeSubAgentResponses(handler, "code-reviewer", "1.0.0", agentContent, digest);

        var service = CreateService(handler);
        await service.SyncOnceAsync(CancellationToken.None);

        var agentPath = Path.Combine(_paths.ServerFeedAgentDirectory("team"), "code-reviewer.md");
        Assert.True(File.Exists(agentPath));
        Assert.Equal(agentContent, File.ReadAllText(agentPath));
        Assert.Equal(Encoding.UTF8.GetBytes(agentContent), await File.ReadAllBytesAsync(agentPath, TestContext.Current.CancellationToken));

        var state = ReadAgentSyncState();
        Assert.Equal("1.0.0", state.Skills["code-reviewer"].Version);
        Assert.Equal(digest, state.Skills["code-reviewer"].Sha256);
    }

    [Fact]
    public async Task SyncOnce_missing_native_sidecar_preserves_rfc_skill_sync()
    {
        var skillContent = "---\nname: feed-skill\ndescription: Feed skill\n---\n\n# Feed Skill\n";
        var digest = SkillSyncHelpers.ComputeSha256(skillContent);

        var handler = new FakeHttpMessageHandler();
        handler.AddStringResponse(
            BaseUrl + ".well-known/agent-skills/index.json",
            $$"""
            {
              "skills": [
                {
                  "name": "feed-skill",
                  "type": "skill",
                  "description": "Feed skill",
                  "url": "{{BaseUrl}}skills/feed-skill/1.0.0/SKILL.md",
                  "digest": "sha256:{{digest}}",
                  "version": "1.0.0"
                }
              ]
            }
            """,
            "application/json");
        handler.AddStringResponse(BaseUrl + "skills/feed-skill/1.0.0/SKILL.md", skillContent, "text/markdown");
        handler.AddErrorResponse(BaseUrl + "subagents/v1/index.json", HttpStatusCode.NotFound);

        var service = CreateService(handler);
        await service.SyncOnceAsync(CancellationToken.None);

        var skillPath = Path.Combine(_paths.ServerFeedDirectory("team"), "feed-skill", "SKILL.md");
        Assert.True(File.Exists(skillPath));
        Assert.Equal(skillContent, File.ReadAllText(skillPath));
        Assert.False(Directory.Exists(_paths.ServerFeedAgentDirectory("team")));
    }

    [Fact]
    public async Task SyncOnce_digest_failure_keeps_existing_managed_subagents_and_skips_prune()
    {
        var agentDir = _paths.ServerFeedAgentDirectory("team");
        Directory.CreateDirectory(agentDir);
        var oldContent = AgentMarkdown("code-reviewer", "Old reviewer", "Old body.");
        File.WriteAllText(Path.Combine(agentDir, "code-reviewer.md"), oldContent);
        File.WriteAllText(Path.Combine(agentDir, "stale-agent.md"), AgentMarkdown("stale-agent", "Stale", "Stale body."));

        SkillSyncHelpers.WriteSyncState(_paths.ServerFeedAgentSyncStatePath("team"), new SkillSyncState
        {
            Skills =
            {
                ["code-reviewer"] = new SyncedSkillState
                {
                    Version = "0.9.0",
                    Sha256 = SkillSyncHelpers.ComputeSha256(oldContent)
                },
                ["stale-agent"] = new SyncedSkillState
                {
                    Version = "0.9.0",
                    Sha256 = "stale"
                }
            }
        });

        var expectedContent = AgentMarkdown("code-reviewer", "New reviewer", "New body.");
        var deliveredContent = AgentMarkdown("code-reviewer", "Tampered reviewer", "Tampered body.");
        var expectedDigest = SkillSyncHelpers.ComputeSha256(expectedContent);

        var handler = new FakeHttpMessageHandler();
        AddEmptyRfcIndex(handler);
        AddNativeSubAgentResponses(handler, "code-reviewer", "1.0.0", deliveredContent, expectedDigest);

        var service = CreateService(handler);
        await service.SyncOnceAsync(CancellationToken.None);

        Assert.Equal(oldContent, File.ReadAllText(Path.Combine(agentDir, "code-reviewer.md")));
        Assert.True(File.Exists(Path.Combine(agentDir, "stale-agent.md")));

        var state = ReadAgentSyncState();
        Assert.True(state.Skills.ContainsKey("stale-agent"));
        Assert.Equal("0.9.0", state.Skills["code-reviewer"].Version);
    }

    [Fact]
    public async Task SyncOnce_invalid_native_artifact_keeps_existing_managed_subagents_and_skips_prune()
    {
        var agentDir = _paths.ServerFeedAgentDirectory("team");
        Directory.CreateDirectory(agentDir);
        var oldContent = AgentMarkdown("code-reviewer", "Old reviewer", "Old body.");
        File.WriteAllText(Path.Combine(agentDir, "code-reviewer.md"), oldContent);
        File.WriteAllText(Path.Combine(agentDir, "stale-agent.md"), AgentMarkdown("stale-agent", "Stale", "Stale body."));

        SkillSyncHelpers.WriteSyncState(_paths.ServerFeedAgentSyncStatePath("team"), new SkillSyncState
        {
            Skills =
            {
                ["code-reviewer"] = new SyncedSkillState
                {
                    Version = "0.9.0",
                    Sha256 = SkillSyncHelpers.ComputeSha256(oldContent)
                },
                ["stale-agent"] = new SyncedSkillState
                {
                    Version = "0.9.0",
                    Sha256 = "stale"
                }
            }
        });

        var invalidContent = """
            ---
            name: code-reviewer
            tools: [file_read]
            ---

            Missing a description, so the runtime loader would reject this artifact.
            """;
        var digest = SkillSyncHelpers.ComputeSha256(invalidContent);

        var handler = new FakeHttpMessageHandler();
        AddEmptyRfcIndex(handler);
        AddNativeSubAgentResponses(handler, "code-reviewer", "1.0.0", invalidContent, digest);

        var service = CreateService(handler);
        await service.SyncOnceAsync(CancellationToken.None);

        Assert.Equal(oldContent, File.ReadAllText(Path.Combine(agentDir, "code-reviewer.md")));
        Assert.True(File.Exists(Path.Combine(agentDir, "stale-agent.md")));

        var state = ReadAgentSyncState();
        Assert.True(state.Skills.ContainsKey("stale-agent"));
        Assert.Equal("0.9.0", state.Skills["code-reviewer"].Version);
    }

    [Fact]
    public async Task SyncOnce_invalid_utf8_native_artifact_keeps_existing_managed_subagents_and_skips_prune()
    {
        var agentDir = _paths.ServerFeedAgentDirectory("team");
        Directory.CreateDirectory(agentDir);
        var oldContent = AgentMarkdown("code-reviewer", "Old reviewer", "Old body.");
        File.WriteAllText(Path.Combine(agentDir, "code-reviewer.md"), oldContent);
        File.WriteAllText(Path.Combine(agentDir, "stale-agent.md"), AgentMarkdown("stale-agent", "Stale", "Stale body."));

        SkillSyncHelpers.WriteSyncState(_paths.ServerFeedAgentSyncStatePath("team"), new SkillSyncState
        {
            Skills =
            {
                ["code-reviewer"] = new SyncedSkillState
                {
                    Version = "0.9.0",
                    Sha256 = SkillSyncHelpers.ComputeSha256(oldContent)
                },
                ["stale-agent"] = new SyncedSkillState
                {
                    Version = "0.9.0",
                    Sha256 = "stale"
                }
            }
        });

        var validPrefix = Encoding.UTF8.GetBytes("""
            ---
            name: code-reviewer
            description: Managed reviewer
            ---

            Review code carefully.
            """);
        var invalidContent = validPrefix.Concat([byte.MaxValue]).ToArray();
        var digest = SkillSyncHelpers.ComputeSha256(invalidContent);

        var handler = new FakeHttpMessageHandler();
        AddEmptyRfcIndex(handler);
        AddNativeSubAgentResponses(handler, "code-reviewer", "1.0.0", invalidContent, digest);

        var service = CreateService(handler);
        await service.SyncOnceAsync(CancellationToken.None);

        Assert.Equal(oldContent, File.ReadAllText(Path.Combine(agentDir, "code-reviewer.md")));
        Assert.True(File.Exists(Path.Combine(agentDir, "stale-agent.md")));

        var state = ReadAgentSyncState();
        Assert.True(state.Skills.ContainsKey("stale-agent"));
        Assert.Equal("0.9.0", state.Skills["code-reviewer"].Version);
    }

    [Fact]
    public async Task SyncOnce_successful_sidecar_prunes_only_removed_managed_subagents()
    {
        var agentDir = _paths.ServerFeedAgentDirectory("team");
        Directory.CreateDirectory(agentDir);
        File.WriteAllText(Path.Combine(agentDir, "stale-agent.md"), AgentMarkdown("stale-agent", "Stale", "Stale body."));
        File.WriteAllText(Path.Combine(_paths.AgentsDirectory, "stale-agent.md"),
            AgentMarkdown("stale-agent", "Local stale", "Local must survive."));

        SkillSyncHelpers.WriteSyncState(_paths.ServerFeedAgentSyncStatePath("team"), new SkillSyncState
        {
            Skills =
            {
                ["stale-agent"] = new SyncedSkillState { Version = "0.9.0", Sha256 = "stale" }
            }
        });

        var agentContent = AgentMarkdown("code-reviewer", "Managed reviewer", "Review code carefully.");
        var digest = SkillSyncHelpers.ComputeSha256(agentContent);

        var handler = new FakeHttpMessageHandler();
        AddEmptyRfcIndex(handler);
        AddNativeSubAgentResponses(handler, "code-reviewer", "1.0.0", agentContent, digest);

        var service = CreateService(handler);
        await service.SyncOnceAsync(CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(agentDir, "stale-agent.md")));
        Assert.True(File.Exists(Path.Combine(_paths.AgentsDirectory, "stale-agent.md")));

        var state = ReadAgentSyncState();
        Assert.Equal(["code-reviewer"], state.Skills.Keys.OrderBy(k => k));
    }

    private ServerFeedSkillSyncService CreateService(ISkillContentScanner? scanner = null)
        => new(
            new SkillFeedsConfig(),
            _paths,
            _skillRegistry,
            _skillIndexPublisher,
            TimeProvider.System,
            scanner ?? new NoOpSkillContentScanner(),
            NullLogger<ServerFeedSkillSyncService>.Instance,
            []);

    private ServerFeedSkillSyncService CreateService(FakeHttpMessageHandler handler)
    {
        var feedsConfig = new SkillFeedsConfig
        {
            SyncIntervalMinutes = 0,
            Feeds = [new SkillFeedSource { Name = "team", Url = BaseUrl, TimeoutSeconds = 30 }]
        };

        return new ServerFeedSkillSyncService(
            feedsConfig,
            _paths,
            _skillRegistry,
            _skillIndexPublisher,
            TimeProvider.System,
            new NoOpSkillContentScanner(),
            NullLogger<ServerFeedSkillSyncService>.Instance,
            [],
            feed =>
            {
                var client = new HttpClient(handler)
                {
                    BaseAddress = new Uri(feed.Url.TrimEnd('/') + "/")
                };
                if (feed.ApiKey is { Value: { Length: > 0 } apiKey })
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                return new SkillServerClient(client);
            });
    }

    private SkillSyncState ReadAgentSyncState()
    {
        var json = File.ReadAllText(_paths.ServerFeedAgentSyncStatePath("team"));
        return JsonSerializer.Deserialize<SkillSyncState>(json)!;
    }

    private static void AddEmptyRfcIndex(FakeHttpMessageHandler handler)
    {
        handler.AddStringResponse(
            BaseUrl + ".well-known/agent-skills/index.json",
            """
            {
              "skills": []
            }
            """,
            "application/json");
    }

    private static void AddNativeSubAgentResponses(
        FakeHttpMessageHandler handler,
        string name,
        string version,
        string artifactContent,
        string expectedDigest)
        => AddNativeSubAgentResponses(handler, name, version, Encoding.UTF8.GetBytes(artifactContent), expectedDigest);

    private static void AddNativeSubAgentResponses(
        FakeHttpMessageHandler handler,
        string name,
        string version,
        byte[] artifactContent,
        string expectedDigest)
    {
        // Use absolute hrefs so the client resolves direct native index traversal correctly.
        handler.AddStringResponse(
            BaseUrl + "subagents/v1/index.json",
            """
            {
              "kind": "subagent-collection-index",
              "links": { "self": { "href": "/subagents/v1/index.json" } },
              "pages": [
                { "range": "a-z", "href": "/subagents/v1/pages/a-z.json" }
              ]
            }
            """,
            "application/json");
        handler.AddStringResponse(
            BaseUrl + "subagents/v1/pages/a-z.json",
            $$"""
            {
              "kind": "subagent-collection-page",
              "range": "a-z",
              "links": { "self": { "href": "/subagents/v1/pages/a-z.json" } },
              "items": [
                {
                  "name": "{{name}}",
                  "latestVersion": "{{version}}",
                  "versionRange": { "min": "{{version}}", "max": "{{version}}", "count": 1 },
                  "href": "/subagents/v1/{{name}}/index.json"
                }
              ]
            }
            """,
            "application/json");
        handler.AddStringResponse(
            BaseUrl + $"subagents/v1/{name}/index.json",
            $$"""
            {
              "kind": "subagent-identity-index",
              "name": "{{name}}",
              "latestVersion": "{{version}}",
              "links": { "self": { "href": "/subagents/v1/{{name}}/index.json" } },
              "versions": [
                {
                  "version": "{{version}}",
                  "publishedAt": "2026-06-30T00:00:00Z",
                  "digest": "sha256:{{expectedDigest}}",
                  "href": "/subagents/v1/{{name}}/versions/{{version}}.json"
                }
              ]
            }
            """,
            "application/json");
        handler.AddStringResponse(
            BaseUrl + $"subagents/v1/{name}/versions/{version}.json",
            $$"""
            {
              "kind": "subagent-version-detail",
              "name": "{{name}}",
              "version": "{{version}}",
              "type": "agent-md",
              "description": "Test sub-agent",
              "url": "{{BaseUrl}}subagents/{{name}}/{{version}}/agent.md",
              "digest": "sha256:{{expectedDigest}}",
              "links": { "self": { "href": "/subagents/v1/{{name}}/versions/{{version}}.json" } }
            }
            """,
            "application/json");
        handler.AddByteResponse(
            BaseUrl + $"subagents/{name}/{version}/agent.md",
            artifactContent,
            "text/markdown");
    }

    private static string AgentMarkdown(string name, string description, string body)
        => $"""
           ---
           name: {name}
           description: {description}
           tools: [file_read]
           ---

           {body}
           """;

    private static byte[] BuildArchive(params (string Path, byte[] Content, int UnixMode)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content, unixMode) in entries)
            {
                var entry = archive.CreateEntry(path);
                entry.ExternalAttributes = unixMode << 16;
                using var entryStream = entry.Open();
                entryStream.Write(content);
            }
        }

        return stream.ToArray();
    }
}
