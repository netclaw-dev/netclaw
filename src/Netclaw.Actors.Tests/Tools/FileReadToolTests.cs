// -----------------------------------------------------------------------
// <copyright file="FileReadToolTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Actors.Skills;
using Netclaw.Actors.Telemetry;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class FileReadToolTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly FileReadTool _tool = new(new ToolConfig());
    private readonly string _sessionDir;

    public FileReadToolTests()
    {
        _sessionDir = Path.Combine(_dir.Path, "session");
        Directory.CreateDirectory(_sessionDir);
    }

    public void Dispose()
    {
        _dir.Dispose();
    }

    [Fact]
    public async Task Read_existing_file_returns_content()
    {
        var filePath = Path.Combine(_dir.Path, "test.txt");
        await File.WriteAllTextAsync(filePath, "hello world", TestContext.Current.CancellationToken);

        var args = new Dictionary<string, object?> { ["Path"] = filePath };
        var result = await _tool.ExecuteAsync(args, CreatePersonalContext(), CancellationToken.None);

        Assert.Equal("hello world", result);
    }

    [Fact]
    public async Task Read_missing_file_returns_error()
    {
        var filePath = Path.Combine(_dir.Path, "nonexistent.txt");
        var args = new Dictionary<string, object?> { ["Path"] = filePath };

        var result = await _tool.ExecuteAsync(args, CreatePersonalContext(), CancellationToken.None);

        Assert.Contains("File not found", result);
    }

    [Fact]
    public async Task Read_with_offset_and_limit()
    {
        var filePath = Path.Combine(_dir.Path, "lines.txt");
        var lines = Enumerable.Range(1, 10).Select(i => $"Line {i}");
        await File.WriteAllLinesAsync(filePath, lines, TestContext.Current.CancellationToken);

        var args = new Dictionary<string, object?>
        {
            ["Path"] = filePath,
            ["Offset"] = 3,
            ["Limit"] = 2
        };

        var result = await _tool.ExecuteAsync(args, CreatePersonalContext(), CancellationToken.None);

        Assert.Contains("Line 3", result);
        Assert.Contains("Line 4", result);
        Assert.DoesNotContain("Line 2", result);
        Assert.DoesNotContain("Line 5", result);
    }

    [Fact]
    public async Task Large_file_is_truncated()
    {
        var tool = new FileReadTool(new ToolConfig { MaxOutputChars = 100 });
        var filePath = Path.Combine(_dir.Path, "large.txt");
        await File.WriteAllTextAsync(filePath, new string('x', 500), TestContext.Current.CancellationToken);

        var args = new Dictionary<string, object?> { ["Path"] = filePath };
        var result = await tool.ExecuteAsync(args, CreatePersonalContext(), CancellationToken.None);

        Assert.Contains("[output truncated]", result);
    }

    [Fact]
    public async Task Missing_path_returns_error()
    {
        var args = new Dictionary<string, object?>();
        var result = await _tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("Path", result);
        Assert.Contains("missing", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Null_arguments_returns_error()
    {
        var result = await _tool.ExecuteAsync(null, CancellationToken.None);
        Assert.Contains("No arguments provided", result);
    }

    [Fact]
    public async Task Read_denied_path_returns_access_denied()
    {
        var filePath = Path.Combine(_dir.Path, "secrets.json");
        await File.WriteAllTextAsync(filePath, """{"secret": "value"}""", TestContext.Current.CancellationToken);

        var policy = new ToolPathPolicy([filePath]);
        var tool = new FileReadTool(new ToolConfig(), policy);
        var args = new Dictionary<string, object?> { ["Path"] = filePath };

        var result = await tool.ExecuteAsync(args, CreatePersonalContext(), CancellationToken.None);

        Assert.Contains("Access denied", result);
        Assert.DoesNotContain("value", result);
    }

    [Fact]
    public async Task Public_context_can_read_file_inside_session_directory()
    {
        var filePath = Path.Combine(_sessionDir, "public-note.txt");
        await File.WriteAllTextAsync(filePath, "session scoped", TestContext.Current.CancellationToken);

        var args = new Dictionary<string, object?> { ["Path"] = filePath };
        var result = await _tool.ExecuteAsync(args, CreatePublicContext(), CancellationToken.None);

        Assert.Equal("session scoped", result);
    }

    [Fact]
    public async Task Public_context_cannot_read_file_outside_session_directory()
    {
        var filePath = Path.Combine(_dir.Path, "host-secret.txt");
        await File.WriteAllTextAsync(filePath, "do not read", TestContext.Current.CancellationToken);

        var args = new Dictionary<string, object?> { ["Path"] = filePath };
        var result = await _tool.ExecuteAsync(args, CreatePublicContext(), CancellationToken.None);

        Assert.Contains("Public trust context", result);
        Assert.Contains("session directory", result);
        Assert.DoesNotContain("do not read", result);
    }

    [Fact]
    public async Task Team_context_can_read_file_inside_skills_directory_via_global_read_roots()
    {
        var skillsDir = Path.Combine(_dir.Path, "skills");
        Directory.CreateDirectory(skillsDir);
        var skillFile = Path.Combine(skillsDir, "test-skill", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(skillFile)!);
        await File.WriteAllTextAsync(skillFile, "# Test Skill", TestContext.Current.CancellationToken);

        var paths = new NetclawPaths(_dir.Path);
        var tool = new FileReadTool(new ToolConfig(), paths: paths);

        var args = new Dictionary<string, object?> { ["Path"] = skillFile };
        var result = await tool.ExecuteAsync(args, CreateTeamContext(), CancellationToken.None);

        Assert.Equal("# Test Skill", result);
    }

    [Fact]
    public async Task Reading_registered_skill_file_records_skill_file_read_telemetry()
    {
        var paths = new NetclawPaths(_dir.Path);
        var registry = new SkillRegistry();
        var metrics = new FakeMetrics();
        var skillFile = Path.Combine(paths.SkillsDirectory, "tracked-skill", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(skillFile)!);
        await File.WriteAllTextAsync(skillFile, "---\nname: tracked-skill\ndescription: tracked\n---\n\n# Tracked", TestContext.Current.CancellationToken);

        var scan = SkillScanner.Scan(paths.SkillsDirectory);
        registry.ReplaceAll(scan.AcceptedSkills, scan.Issues);

        var tool = new FileReadTool(new ToolConfig(), paths: paths, skillRegistry: registry, sessionMetrics: metrics);

        await tool.ExecuteAsync(new Dictionary<string, object?> { ["Path"] = skillFile }, CreateTeamContext(), CancellationToken.None);

        var call = Assert.Single(metrics.SkillLoadedCalls);
        Assert.Equal("tracked-skill", call.SkillName);
        Assert.Equal(SkillLoadMethod.FileRead, call.Method);
    }

    [Fact]
    public async Task Reading_non_skill_file_does_not_record_skill_telemetry()
    {
        var paths = new NetclawPaths(_dir.Path);
        var registry = new SkillRegistry();
        var metrics = new FakeMetrics();
        var filePath = Path.Combine(_sessionDir, "notes.txt");
        await File.WriteAllTextAsync(filePath, "notes", TestContext.Current.CancellationToken);

        var tool = new FileReadTool(new ToolConfig(), paths: paths, skillRegistry: registry, sessionMetrics: metrics);

        await tool.ExecuteAsync(new Dictionary<string, object?> { ["Path"] = filePath }, CreatePersonalContext(), CancellationToken.None);

        Assert.Empty(metrics.SkillLoadedCalls);
    }

    [Fact]
    public async Task Team_context_can_read_file_inside_identity_directory_via_global_read_roots()
    {
        var identityDir = Path.Combine(_dir.Path, "identity");
        Directory.CreateDirectory(identityDir);
        var soulFile = Path.Combine(identityDir, "SOUL.md");
        await File.WriteAllTextAsync(soulFile, "# Soul", TestContext.Current.CancellationToken);

        var paths = new NetclawPaths(_dir.Path);
        var tool = new FileReadTool(new ToolConfig(), paths: paths);

        var args = new Dictionary<string, object?> { ["Path"] = soulFile };
        var result = await tool.ExecuteAsync(args, CreateTeamContext(), CancellationToken.None);

        Assert.Equal("# Soul", result);
    }

    [Fact]
    public async Task Team_context_cannot_read_file_outside_session_and_global_roots()
    {
        var secretFile = Path.Combine(_dir.Path, "config", "secrets.json");
        Directory.CreateDirectory(Path.GetDirectoryName(secretFile)!);
        await File.WriteAllTextAsync(secretFile, "secret data", TestContext.Current.CancellationToken);

        var paths = new NetclawPaths(_dir.Path);
        var tool = new FileReadTool(new ToolConfig(), paths: paths);

        var args = new Dictionary<string, object?> { ["Path"] = secretFile };
        var result = await tool.ExecuteAsync(args, CreateTeamContext(), CancellationToken.None);

        Assert.Contains("Team trust context", result);
        Assert.DoesNotContain("secret data", result);
    }

    [Fact]
    public async Task Team_context_without_paths_falls_back_to_session_only()
    {
        var skillsDir = Path.Combine(_dir.Path, "skills");
        Directory.CreateDirectory(skillsDir);
        var skillFile = Path.Combine(skillsDir, "test-skill", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(skillFile)!);
        await File.WriteAllTextAsync(skillFile, "# Test Skill", TestContext.Current.CancellationToken);

        // No paths injected — no global read roots
        var tool = new FileReadTool(new ToolConfig());

        var args = new Dictionary<string, object?> { ["Path"] = skillFile };
        var result = await tool.ExecuteAsync(args, CreateTeamContext(), CancellationToken.None);

        Assert.Contains("Team trust context", result);
    }

    [Fact]
    public async Task Team_context_can_read_file_inside_literal_global_read_root()
    {
        var sharedDir = Path.Combine(_dir.Path, "shared-data");
        Directory.CreateDirectory(sharedDir);
        var dataFile = Path.Combine(sharedDir, "data.txt");
        await File.WriteAllTextAsync(dataFile, "shared content", TestContext.Current.CancellationToken);

        var config = new ToolConfig
        {
            AudienceProfiles = new ToolAudienceProfiles
            {
                GlobalReadRoots = [sharedDir]
            }
        };
        var paths = new NetclawPaths(_dir.Path);
        var tool = new FileReadTool(config, paths: paths);

        var args = new Dictionary<string, object?> { ["Path"] = dataFile };
        var result = await tool.ExecuteAsync(args, CreateTeamContext(), CancellationToken.None);

        Assert.Equal("shared content", result);
    }

    [Fact]
    public async Task Literal_global_read_root_works_without_netclaw_paths()
    {
        var sharedDir = Path.Combine(_dir.Path, "shared-data");
        Directory.CreateDirectory(sharedDir);
        var dataFile = Path.Combine(sharedDir, "data.txt");
        await File.WriteAllTextAsync(dataFile, "shared content", TestContext.Current.CancellationToken);

        var config = new ToolConfig
        {
            AudienceProfiles = new ToolAudienceProfiles
            {
                GlobalReadRoots = [sharedDir]
            }
        };
        // No NetclawPaths injected — literal paths should still resolve
        var tool = new FileReadTool(config);

        var args = new Dictionary<string, object?> { ["Path"] = dataFile };
        var result = await tool.ExecuteAsync(args, CreateTeamContext(), CancellationToken.None);

        Assert.Equal("shared content", result);
    }

    private ToolExecutionContext CreatePersonalContext()
        => new("signalr/thread-1", _sessionDir)
        {
            Audience = TrustAudience.Personal.ToWireValue(),
            Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
            ChannelType = "signalr"
        };

    private ToolExecutionContext CreateTeamContext()
        => new("slack/thread-1", _sessionDir)
        {
            Audience = TrustAudience.Team.ToWireValue(),
            Boundary = SecurityPolicyDefaults.TeamBoundary,
            ChannelType = "slack"
        };

    private ToolExecutionContext CreatePublicContext()
        => new("slack/thread-1", _sessionDir)
        {
            Audience = TrustAudience.Public.ToWireValue(),
            Boundary = SecurityPolicyDefaults.PublicBoundary,
            ChannelType = "slack"
        };

    private sealed class FakeMetrics : ISessionMetrics
    {
        public List<(string SkillName, SkillLoadMethod Method)> SkillLoadedCalls { get; } = [];

        public void RecordTokenUsage(long inputTokens, long outputTokens) { }
        public void RecordTurnCompleted() { }
        public void RecordSessionCreated() { }
        public void RecordMemoriesFormed(int count) { }
        public void RecordMemoriesRecalled(int count) { }
        public void RecordSkillsLoaded(int count) { }

        public void RecordSkillLoaded(string skillName, SkillLoadMethod method)
            => SkillLoadedCalls.Add((skillName, method));
    }
}
