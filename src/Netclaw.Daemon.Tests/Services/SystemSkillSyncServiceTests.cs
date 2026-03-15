using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;
using Netclaw.Daemon.Services;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class SystemSkillSyncServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;
    private readonly SkillRegistry _skillRegistry;
    private readonly SkillIndexContextLayer _skillIndexLayer;

    public SystemSkillSyncServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        _paths = new NetclawPaths(_tempDir);
        _paths.EnsureDirectoriesExist();
        _skillRegistry = new SkillRegistry();
        _skillIndexLayer = new SkillIndexContextLayer();
    }

    [Fact]
    public async Task StartAsync_SyncsNewSkillFromFeed()
    {
        var skillContent = "---\nname: test-skill\ndescription: A test skill\n---\n\n# Test Skill\n\nSome instructions.";
        var sha256 = SystemSkillSyncService.ComputeSha256(skillContent);

        var manifest = new SkillFeedManifest
        {
            Skills =
            [
                new SkillFeedEntry
                {
                    Name = "test-skill",
                    Version = "1.0.0",
                    Sha256 = sha256,
                    SizeBytes = skillContent.Length,
                    Url = "https://feeds.netclaw.dev/skills/.system/files/test-skill/1.0.0/SKILL.md"
                }
            ]
        };

        var handler = new FakeHttpHandler();
        handler.AddJsonResponse(FeedConstants.SystemSkillsManifestUrl, manifest);
        handler.AddStringResponse(manifest.Skills[0].Url, skillContent);

        var sut = CreateService(handler);
        await sut.StartAsync(CancellationToken.None);

        // Skill should be written as directory-based: test-skill/SKILL.md
        var skillPath = Path.Combine(_paths.SystemSkillsDirectory, "test-skill", "SKILL.md");
        Assert.True(File.Exists(skillPath));
        Assert.Equal(skillContent, File.ReadAllText(skillPath));

        // Sync state should be updated
        var syncState = ReadSyncState();
        Assert.True(syncState.Skills.ContainsKey("test-skill"));
        Assert.Equal("1.0.0", syncState.Skills["test-skill"].Version);

        // Registry should have the skill
        Assert.Single(_skillRegistry.GetAll());
    }

    [Fact]
    public async Task StartAsync_SkipFeedSync_WhenDisableSystemSkillSyncTrue()
    {
        // Pre-populate a directory-based skill
        var localDir = Path.Combine(_paths.SystemSkillsDirectory, "local-only");
        Directory.CreateDirectory(localDir);
        var localContent = "---\nname: local-only\ndescription: Local\n---\n\n# Local Skill\n\nLocal instructions.";
        File.WriteAllText(Path.Combine(localDir, "SKILL.md"), localContent);

        var manifest = new SkillFeedManifest
        {
            Skills =
            [
                new SkillFeedEntry
                {
                    Name = "remote-skill",
                    Version = "1.0.0",
                    Sha256 = SystemSkillSyncService.ComputeSha256("# Remote"),
                    SizeBytes = 8,
                    Url = "https://feeds.netclaw.dev/skills/.system/files/remote-skill/1.0.0/SKILL.md"
                }
            ]
        };

        var handler = new FakeHttpHandler();
        handler.AddJsonResponse(FeedConstants.SystemSkillsManifestUrl, manifest);
        handler.AddStringResponse(manifest.Skills[0].Url, "# Remote");

        var service = new SystemSkillSyncService(
            new HttpClient(handler),
            _paths,
            new SkillSyncConfig { DisableSystemSkillSync = true },
            _skillRegistry,
            _skillIndexLayer,
            TimeProvider.System,
            NullLogger<SystemSkillSyncService>.Instance,
            "0.1.0");

        await service.StartAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(localDir, "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(_paths.SystemSkillsDirectory, "remote-skill")));
        Assert.False(File.Exists(_paths.SkillSyncStatePath));
    }

    [Fact]
    public async Task StartAsync_SkipsSkillWhenVersionAlreadySynced()
    {
        var skillContent = "---\nname: existing\ndescription: Already here\n---\n\n# Already Here\n\nContent.";
        var sha256 = SystemSkillSyncService.ComputeSha256(skillContent);

        // Pre-populate the skill directory and sync state
        var skillDir = Path.Combine(_paths.SystemSkillsDirectory, "existing");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), skillContent);
        WriteSyncState(new SkillSyncState
        {
            Skills =
            {
                ["existing"] = new SyncedSkillState
                {
                    Version = "1.0.0",
                    Sha256 = sha256,
                    SyncedAtUtc = DateTimeOffset.UtcNow
                }
            }
        });

        var manifest = new SkillFeedManifest
        {
            Skills =
            [
                new SkillFeedEntry
                {
                    Name = "existing",
                    Version = "1.0.0",
                    Sha256 = sha256,
                    SizeBytes = skillContent.Length,
                    Url = "https://feeds.netclaw.dev/skills/.system/files/existing/1.0.0/SKILL.md"
                }
            ]
        };

        var handler = new FakeHttpHandler();
        handler.AddJsonResponse(FeedConstants.SystemSkillsManifestUrl, manifest);
        // No download response added — should not be called

        var sut = CreateService(handler);
        await sut.StartAsync(CancellationToken.None);

        // Verify the file wasn't re-downloaded (handler would throw if URL was hit)
        Assert.Equal(skillContent, File.ReadAllText(Path.Combine(skillDir, "SKILL.md")));
    }

    [Fact]
    public async Task StartAsync_DownloadsUpdatedVersion()
    {
        var oldContent = "---\nname: my-skill\ndescription: Old\n---\n\n# Old\n\nOld content.";
        var newContent = "---\nname: my-skill\ndescription: Updated\n---\n\n# Updated\n\nNew content.";
        var oldSha = SystemSkillSyncService.ComputeSha256(oldContent);
        var newSha = SystemSkillSyncService.ComputeSha256(newContent);

        var skillDir = Path.Combine(_paths.SystemSkillsDirectory, "my-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), oldContent);
        WriteSyncState(new SkillSyncState
        {
            Skills =
            {
                ["my-skill"] = new SyncedSkillState
                {
                    Version = "1.0.0",
                    Sha256 = oldSha,
                    SyncedAtUtc = DateTimeOffset.UtcNow
                }
            }
        });

        var manifest = new SkillFeedManifest
        {
            Skills =
            [
                new SkillFeedEntry
                {
                    Name = "my-skill",
                    Version = "1.1.0",
                    Sha256 = newSha,
                    SizeBytes = newContent.Length,
                    Url = "https://feeds.netclaw.dev/skills/.system/files/my-skill/1.1.0/SKILL.md"
                }
            ]
        };

        var handler = new FakeHttpHandler();
        handler.AddJsonResponse(FeedConstants.SystemSkillsManifestUrl, manifest);
        handler.AddStringResponse(manifest.Skills[0].Url, newContent);

        var sut = CreateService(handler);
        await sut.StartAsync(CancellationToken.None);

        var onDisk = File.ReadAllText(Path.Combine(skillDir, "SKILL.md"));
        Assert.Equal(newContent, onDisk);

        var state = ReadSyncState();
        Assert.Equal("1.1.0", state.Skills["my-skill"].Version);
    }

    [Fact]
    public async Task StartAsync_RejectsDownloadWithBadChecksum()
    {
        var skillContent = "# Good Content";
        var badContent = "# Tampered Content";
        var correctSha = SystemSkillSyncService.ComputeSha256(skillContent);

        var manifest = new SkillFeedManifest
        {
            Skills =
            [
                new SkillFeedEntry
                {
                    Name = "tampered",
                    Version = "1.0.0",
                    Sha256 = correctSha, // expects hash of skillContent
                    SizeBytes = skillContent.Length,
                    Url = "https://feeds.netclaw.dev/skills/.system/files/tampered/1.0.0/SKILL.md"
                }
            ]
        };

        var handler = new FakeHttpHandler();
        handler.AddJsonResponse(FeedConstants.SystemSkillsManifestUrl, manifest);
        handler.AddStringResponse(manifest.Skills[0].Url, badContent); // delivers tampered content

        var sut = CreateService(handler);
        await sut.StartAsync(CancellationToken.None);

        // Skill directory should NOT contain SKILL.md (dir created but content rejected)
        var skillMd = Path.Combine(_paths.SystemSkillsDirectory, "tampered", "SKILL.md");
        Assert.False(File.Exists(skillMd));
    }

    [Fact]
    public async Task StartAsync_SkipsSkillRequiringNewerDaemon()
    {
        var skillContent = "# Future Skill";
        var sha256 = SystemSkillSyncService.ComputeSha256(skillContent);

        var manifest = new SkillFeedManifest
        {
            Skills =
            [
                new SkillFeedEntry
                {
                    Name = "future-skill",
                    Version = "1.0.0",
                    MinimumDaemonVersion = "99.0.0",
                    Sha256 = sha256,
                    SizeBytes = skillContent.Length,
                    Url = "https://feeds.netclaw.dev/skills/.system/files/future-skill/1.0.0/SKILL.md"
                }
            ]
        };

        var handler = new FakeHttpHandler();
        handler.AddJsonResponse(FeedConstants.SystemSkillsManifestUrl, manifest);

        var sut = CreateService(handler, daemonVersion: "0.1.0");
        await sut.StartAsync(CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(_paths.SystemSkillsDirectory, "future-skill")));
    }

    [Fact]
    public async Task StartAsync_GracefullyHandlesNetworkFailure()
    {
        // Pre-populate a skill so we verify it's still usable after failure
        var existingDir = Path.Combine(_paths.SystemSkillsDirectory, "existing");
        Directory.CreateDirectory(existingDir);
        File.WriteAllText(Path.Combine(existingDir, "SKILL.md"),
            "---\nname: existing\ndescription: Still here\n---\n\n# Existing\n\nContent.");

        var handler = new FakeHttpHandler();
        handler.AddErrorResponse(FeedConstants.SystemSkillsManifestUrl, HttpStatusCode.ServiceUnavailable);

        var sut = CreateService(handler);
        await sut.StartAsync(CancellationToken.None);

        // Existing skill should still be in registry
        Assert.Single(_skillRegistry.GetAll());
        Assert.Contains(_skillRegistry.GetAll(), s => s.Name == "existing");
    }

    [Fact]
    public async Task StartAsync_PicksUpUserSkillsAlongWithSystemSkills()
    {
        // System skill in .system/skill-name/SKILL.md
        var systemDir = Path.Combine(_paths.SystemSkillsDirectory, "system-skill");
        Directory.CreateDirectory(systemDir);
        File.WriteAllText(Path.Combine(systemDir, "SKILL.md"),
            "---\nname: system-skill\ndescription: system skill\n---\n\n# System\n\nContent.");

        // User skill in skills/skill-name/SKILL.md
        var userDir = Path.Combine(_paths.SkillsDirectory, "user-skill");
        Directory.CreateDirectory(userDir);
        File.WriteAllText(Path.Combine(userDir, "SKILL.md"),
            "---\nname: user-skill\ndescription: user skill\n---\n\n# User\n\nContent.");

        var handler = new FakeHttpHandler();
        handler.AddErrorResponse(FeedConstants.SystemSkillsManifestUrl, HttpStatusCode.ServiceUnavailable);

        var sut = CreateService(handler);
        await sut.StartAsync(CancellationToken.None);

        // Both skills should be in the registry
        var all = _skillRegistry.GetAll();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, s => s.Name == "system-skill");
        Assert.Contains(all, s => s.Name == "user-skill");
    }

    [Fact]
    public void IsVersionSatisfied_CurrentGreaterThanMinimum()
    {
        Assert.True(SystemSkillSyncService.IsVersionSatisfied("0.2.0", "0.1.0"));
    }

    [Fact]
    public void IsVersionSatisfied_CurrentEqualsMinimum()
    {
        Assert.True(SystemSkillSyncService.IsVersionSatisfied("0.1.0", "0.1.0"));
    }

    [Fact]
    public void IsVersionSatisfied_CurrentLessThanMinimum()
    {
        Assert.False(SystemSkillSyncService.IsVersionSatisfied("0.1.0", "0.2.0"));
    }

    [Fact]
    public void ComputeSha256_ProducesConsistentHash()
    {
        var content = "Hello, world!";
        var hash1 = SystemSkillSyncService.ComputeSha256(content);
        var hash2 = SystemSkillSyncService.ComputeSha256(content);
        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length); // SHA-256 = 32 bytes = 64 hex chars
    }

    [Fact]
    public async Task StartAsync_FallbackKeywordIndexCapturesTriggerPhrases()
    {
        var skillDir = Path.Combine(_paths.SystemSkillsDirectory, "netclaw-diagnostics");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            "---\nname: netclaw-diagnostics\ndescription: diagnose daemon issues\nmetadata:\n  triggers: session timeout | missing tools | check logs\n---\n\n# Self Diagnostics\n");

        var handler = new FakeHttpHandler();
        handler.AddErrorResponse(FeedConstants.SystemSkillsManifestUrl, HttpStatusCode.ServiceUnavailable);

        var sut = CreateService(handler);
        await sut.StartAsync(CancellationToken.None);

        var index = Assert.Contains("netclaw-diagnostics", _skillRegistry.GetEnrichedKeywords());
        Assert.Contains("session timeout", index.Phrases);
        Assert.Contains("missing tool", index.Phrases);
        Assert.DoesNotContain("check", index.Keywords);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private SystemSkillSyncService CreateService(FakeHttpHandler handler, string daemonVersion = "0.1.0")
    {
        var httpClient = new HttpClient(handler);
        return new SystemSkillSyncService(
            httpClient,
            _paths,
            new SkillSyncConfig(),
            _skillRegistry,
            _skillIndexLayer,
            TimeProvider.System,
            NullLogger<SystemSkillSyncService>.Instance,
            daemonVersion);
    }

    private SkillSyncState ReadSyncState()
    {
        var json = File.ReadAllText(_paths.SkillSyncStatePath);
        return JsonSerializer.Deserialize<SkillSyncState>(json)!;
    }

    private void WriteSyncState(SkillSyncState state)
    {
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_paths.SkillSyncStatePath, json);
    }

    /// <summary>
    /// Simple fake HTTP handler that returns pre-configured responses by URL.
    /// </summary>
    internal sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Content, string ContentType)> _responses = new();

        public void AddJsonResponse<T>(string url, T body)
        {
            var json = JsonSerializer.Serialize(body);
            _responses[url] = (HttpStatusCode.OK, json, "application/json");
        }

        public void AddStringResponse(string url, string content)
        {
            _responses[url] = (HttpStatusCode.OK, content, "text/markdown");
        }

        public void AddErrorResponse(string url, HttpStatusCode status)
        {
            _responses[url] = (status, string.Empty, "text/plain");
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (!_responses.TryGetValue(url, out var entry))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var response = new HttpResponseMessage(entry.Status)
            {
                Content = new StringContent(entry.Content, System.Text.Encoding.UTF8, entry.ContentType)
            };
            return Task.FromResult(response);
        }
    }
}
