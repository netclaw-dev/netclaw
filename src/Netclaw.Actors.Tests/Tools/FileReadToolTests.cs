// -----------------------------------------------------------------------
// <copyright file="FileReadToolTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
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

        var args = ToolInput.Create("Path", filePath);
        var result = await _tool.ExecuteAsync(args, CreatePersonalContext(), CancellationToken.None);

        Assert.Equal("hello world", result);
    }

    [Fact]
    public async Task Read_code_file_with_unknown_mime_returns_content()
    {
        var filePath = Path.Combine(_dir.Path, "Program.cs");
        await File.WriteAllTextAsync(filePath, "public static class Program { }", TestContext.Current.CancellationToken);

        var args = ToolInput.Create("Path", filePath);
        var result = await _tool.ExecuteAsync(args, CreatePersonalContext(), CancellationToken.None);

        Assert.Equal("public static class Program { }", result);
    }

    [Fact]
    public async Task Read_utf16_text_with_bom_returns_content()
    {
        var filePath = Path.Combine(_dir.Path, "notes.txt");
        const string expected = "first line\nsecond line";
        await File.WriteAllTextAsync(filePath, expected, Encoding.Unicode, TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(ToolInput.Create("Path", filePath), CreatePersonalContext(), CancellationToken.None);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task Read_windows1252_text_extension_returns_content()
    {
        var filePath = Path.Combine(_dir.Path, "notes.txt");
        var bytes = new byte[]
        {
            (byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)' ', 0x93,
            (byte)'q', (byte)'u', (byte)'o', (byte)'t', (byte)'e', (byte)'d',
            0x94, (byte)' ', 0x97, (byte)' ', (byte)'d', (byte)'o', (byte)'n', (byte)'e'
        };
        await File.WriteAllBytesAsync(filePath, bytes, TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(ToolInput.Create("Path", filePath), CreatePersonalContext(), CancellationToken.None);

        Assert.Equal("caf\u00E9 \u201Cquoted\u201D \u2014 done", result);
    }

    [Fact]
    public async Task Read_utf8_text_with_split_sample_boundary_returns_content()
    {
        var filePath = Path.Combine(_dir.Path, "boundary.txt");
        var expected = new string('a', 4095) + "\u20AC after boundary";
        await File.WriteAllTextAsync(filePath, expected, StrictUtf8NoBom, TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(ToolInput.Create("Path", filePath), CreatePersonalContext(), CancellationToken.None);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task Image_read_on_image_capable_model_registers_model_input_file()
    {
        var filePath = Path.Combine(_dir.Path, "diagram.png");
        await File.WriteAllBytesAsync(filePath, FakePngBytes, TestContext.Current.CancellationToken);
        var context = CreatePersonalContext();
        context.ModelInputModalities = ModelModality.Text | ModelModality.Image;

        var result = await _tool.ExecuteAsync(ToolInput.Create("Path", filePath), context, CancellationToken.None);

        Assert.Contains("Image loaded for model-visible inspection", result);
        var modelInput = Assert.Single(context.ModelInputFiles);
        Assert.Equal(filePath, modelInput.FilePath);
        Assert.Equal("diagram.png", modelInput.FileName);
        Assert.Equal("image/png", modelInput.MimeType);
    }

    [Fact]
    public async Task Image_read_on_text_only_model_returns_modality_guidance()
    {
        var filePath = Path.Combine(_dir.Path, "diagram.png");
        await File.WriteAllBytesAsync(filePath, FakePngBytes, TestContext.Current.CancellationToken);
        var context = CreatePersonalContext();

        var result = await _tool.ExecuteAsync(ToolInput.Create("Path", filePath), context, CancellationToken.None);

        Assert.Contains("current model has no image modality", result);
        Assert.Empty(context.ModelInputFiles);
    }

    [Fact]
    public async Task Image_extension_without_image_magic_returns_binary_guidance()
    {
        var filePath = Path.Combine(_dir.Path, "diagram.png");
        await File.WriteAllBytesAsync(filePath, [0, 1, 2, 3, 4, 5, 6, 7], TestContext.Current.CancellationToken);
        var context = CreatePersonalContext();
        context.ModelInputModalities = ModelModality.Text | ModelModality.Image;

        var result = await _tool.ExecuteAsync(ToolInput.Create("Path", filePath), context, CancellationToken.None);

        Assert.Contains("application/octet-stream", result);
        Assert.Empty(context.ModelInputFiles);
    }

    [Fact]
    public async Task Pdf_read_returns_metadata_without_extracting_text()
    {
        var filePath = Path.Combine(_dir.Path, "report.pdf");
        await File.WriteAllBytesAsync(filePath, FakePdfBytes, TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(ToolInput.Create("Path", filePath), CreatePersonalContext(), CancellationToken.None);

        Assert.Contains("Type: application/pdf (Pdf)", result);
        Assert.Contains("Native PDF extraction is not built into file_read", result);
        Assert.DoesNotContain("fake body", result);
    }

    [Fact]
    public async Task Unknown_binary_read_returns_guidance_instead_of_raw_bytes()
    {
        var filePath = Path.Combine(_dir.Path, "payload.bin");
        await File.WriteAllBytesAsync(filePath, [0, 1, 2, 3, 4, 5, 6, 7], TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(ToolInput.Create("Path", filePath), CreatePersonalContext(), CancellationToken.None);

        Assert.Contains("application/octet-stream", result);
        Assert.Contains("Raw binary output is not returned by file_read", result);
    }

    [Fact]
    public async Task Text_extension_with_binary_content_returns_guidance_instead_of_raw_bytes()
    {
        var filePath = Path.Combine(_dir.Path, "payload.json");
        await File.WriteAllBytesAsync(filePath, [0, 1, 2, 3, 4, 5, 6, 7], TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(ToolInput.Create("Path", filePath), CreatePersonalContext(), CancellationToken.None);

        Assert.Contains("application/octet-stream", result);
        Assert.Contains("Raw binary output is not returned by file_read", result);
    }

    [Fact]
    public async Task Legacy_office_document_returns_document_guidance()
    {
        var filePath = Path.Combine(_dir.Path, "report.doc");
        await File.WriteAllBytesAsync(filePath, FakeOleDocumentBytes, TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(ToolInput.Create("Path", filePath), CreatePersonalContext(), CancellationToken.None);

        Assert.Contains("Type: application/msword (Document)", result);
        Assert.Contains("Binary document extraction is not built into file_read", result);
    }

    [Fact]
    public async Task Read_missing_file_returns_error()
    {
        var filePath = Path.Combine(_dir.Path, "nonexistent.txt");
        var args = ToolInput.Create("Path", filePath);

        var result = await _tool.ExecuteAsync(args, CreatePersonalContext(), CancellationToken.None);

        Assert.Contains("File not found", result);
    }

    [Fact]
    public async Task Read_with_offset_and_limit()
    {
        var filePath = Path.Combine(_dir.Path, "lines.txt");
        var lines = Enumerable.Range(1, 10).Select(i => $"Line {i}");
        await File.WriteAllLinesAsync(filePath, lines, TestContext.Current.CancellationToken);

        var args = ToolInput.Create("Path", filePath, "Offset", 3, "Limit", 2);

        var result = await _tool.ExecuteAsync(args, CreatePersonalContext(), CancellationToken.None);

        Assert.Contains("Line 3", result);
        Assert.Contains("Line 4", result);
        Assert.DoesNotContain("Line 2", result);
        Assert.DoesNotContain("Line 5", result);
    }

    [Fact]
    public async Task Large_file_is_truncated_with_continuation_hint()
    {
        var tool = new FileReadTool(new ToolConfig { MaxOutputChars = 100 });
        var filePath = Path.Combine(_dir.Path, "large.txt");
        await File.WriteAllTextAsync(filePath, new string('x', 500), TestContext.Current.CancellationToken);

        var args = ToolInput.Create("Path", filePath);
        var result = await tool.ExecuteAsync(args, CreatePersonalContext(), CancellationToken.None);

        Assert.Contains("output truncated", result);
        Assert.Contains("Offset=", result);
    }

    [Fact]
    public async Task Paginated_read_truncated_by_char_limit_includes_continuation_hint()
    {
        var tool = new FileReadTool(new ToolConfig { MaxOutputChars = 50 });
        var filePath = Path.Combine(_dir.Path, "paged.txt");
        var lines = Enumerable.Range(1, 20).Select(i => $"Line {i:D2} content here");
        await File.WriteAllLinesAsync(filePath, lines, TestContext.Current.CancellationToken);

        var args = ToolInput.Create("Path", filePath, "Offset", 1, "Limit", 20);
        var result = await tool.ExecuteAsync(args, CreatePersonalContext(), CancellationToken.None);

        Assert.Contains("output truncated", result);
        Assert.Contains("Offset=", result);
    }

    [Fact]
    public async Task Missing_path_returns_error()
    {
        var args = ToolInput.Empty();
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
        var args = ToolInput.Create("Path", filePath);

        var result = await tool.ExecuteAsync(args, CreatePersonalContext(), CancellationToken.None);

        Assert.Contains("Access denied", result);
        Assert.DoesNotContain("value", result);
    }

    [Fact]
    public async Task Public_context_can_read_file_inside_session_directory()
    {
        var filePath = Path.Combine(_sessionDir, "public-note.txt");
        await File.WriteAllTextAsync(filePath, "session scoped", TestContext.Current.CancellationToken);

        var args = ToolInput.Create("Path", filePath);
        var result = await _tool.ExecuteAsync(args, CreatePublicContext(), CancellationToken.None);

        Assert.Equal("session scoped", result);
    }

    [Fact]
    public async Task Public_context_cannot_read_file_outside_session_directory()
    {
        var filePath = Path.Combine(_dir.Path, "host-secret.txt");
        await File.WriteAllTextAsync(filePath, "do not read", TestContext.Current.CancellationToken);

        var args = ToolInput.Create("Path", filePath);
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

        var args = ToolInput.Create("Path", skillFile);
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

        await tool.ExecuteAsync(ToolInput.Create("Path", skillFile), CreateTeamContext(), CancellationToken.None);

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

        await tool.ExecuteAsync(ToolInput.Create("Path", filePath), CreatePersonalContext(), CancellationToken.None);

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

        var args = ToolInput.Create("Path", soulFile);
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

        var args = ToolInput.Create("Path", secretFile);
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

        var args = ToolInput.Create("Path", skillFile);
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

        var args = ToolInput.Create("Path", dataFile);
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

        var args = ToolInput.Create("Path", dataFile);
        var result = await tool.ExecuteAsync(args, CreateTeamContext(), CancellationToken.None);

        Assert.Equal("shared content", result);
    }

    private ToolExecutionContext CreatePersonalContext()
        => new("signalr/thread-1", _sessionDir)
        {
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.TrustedInstance,
            ChannelType = "signalr"
        };

    private ToolExecutionContext CreateTeamContext()
        => new("slack/thread-1", _sessionDir)
        {
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            ChannelType = "slack"
        };

    private ToolExecutionContext CreatePublicContext()
        => new("slack/thread-1", _sessionDir)
        {
            Audience = TrustAudience.Public,
            Boundary = TrustBoundary.Public,
            ChannelType = "slack"
        };

    private static readonly byte[] FakePngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52
    ];

    private static readonly byte[] FakePdfBytes = "%PDF-1.7\nfake body\n%%EOF"u8.ToArray();

    private static readonly byte[] FakeOleDocumentBytes =
    [
        0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1,
        0x00, 0x01, 0x02, 0x03
    ];

    private static readonly Encoding StrictUtf8NoBom = new UTF8Encoding(false, true);

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
