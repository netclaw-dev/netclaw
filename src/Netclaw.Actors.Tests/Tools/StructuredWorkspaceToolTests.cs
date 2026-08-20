// -----------------------------------------------------------------------
// <copyright file="StructuredWorkspaceToolTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using System.Text.Json;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Media;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public sealed class StructuredWorkspaceToolTests : IDisposable
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    private readonly DisposableTempDir _temp = new();
    private readonly string _project;
    private readonly string _session;
    private readonly ToolConfig _config = new();
    private readonly ToolPathPolicy _openPathPolicy = new([]);

    public StructuredWorkspaceToolTests()
    {
        _project = Path.Join(_temp.Path, "project");
        _session = Path.Join(_temp.Path, "sessions", "current");
        Directory.CreateDirectory(_project);
        Directory.CreateDirectory(_session);
    }

    public void Dispose() => _temp.Dispose();

    [Fact(SkipUnless = nameof(IsPosix), Skip = "Directory symlink traversal requires native POSIX semantics.")]
    [SlopwatchSuppress("SW001", "This regression requires native POSIX symbolic-link traversal semantics.")]
    public async Task File_search_is_deterministic_and_does_not_follow_directory_symlinks()
    {
        var nested = Path.Join(_project, "nested");
        var outside = Path.Join(_temp.Path, "outside");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Join(_project, "z.txt"), "needle z", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Join(_project, "a.txt"), "needle a", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Join(nested, "b.txt"), "needle b", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Join(outside, "escaped.txt"), "needle escaped", TestContext.Current.CancellationToken);
        Directory.CreateSymbolicLink(Path.Join(_project, "escape"), outside);
        var context = CreateContext();
        var tool = new FileSearchTool(_config, new NetclawPaths(), _openPathPolicy);

        var result = await tool.ExecuteAsync(
            SearchInput(".", "needle", "content", 10, 20, 4096),
            context,
            TestContext.Current.CancellationToken);

        Assert.True(result.IndexOf("a.txt:1", StringComparison.Ordinal) < result.IndexOf("nested/b.txt:1", StringComparison.Ordinal));
        Assert.True(result.IndexOf("nested/b.txt:1", StringComparison.Ordinal) < result.IndexOf("z.txt:1", StringComparison.Ordinal));
        Assert.DoesNotContain("escaped.txt", result, StringComparison.Ordinal);
        Assert.Contains("skipped=1", result, StringComparison.Ordinal);
        Assert.Contains("truncated=false", result, StringComparison.Ordinal);
        Assert.Equal(ToolInvocationOutcomeCategory.Success, context.Receipt?.Category);
        Assert.Empty(context.Receipt?.FileActivity ?? []);
    }

    [Fact]
    public async Task File_search_result_ceiling_sets_truncation_and_name_mode_is_literal()
    {
        await File.WriteAllTextAsync(Path.Join(_project, "alpha-one.txt"), "x", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Join(_project, "alpha-two.txt"), "x", TestContext.Current.CancellationToken);
        var context = CreateContext();
        var tool = new FileSearchTool(_config, new NetclawPaths(), _openPathPolicy);

        var result = await tool.ExecuteAsync(
            SearchInput(".", "alpha-", "name", 1, 10, 1),
            context,
            TestContext.Current.CancellationToken);

        Assert.Contains("matches=1", result, StringComparison.Ordinal);
        Assert.Contains("alpha-one.txt", result, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha-two.txt", result, StringComparison.Ordinal);
        Assert.Contains("truncated=true", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_search_stops_at_entry_and_content_byte_ceilings()
    {
        for (var index = 0; index < 5; index++)
        {
            await File.WriteAllTextAsync(
                Path.Join(_project, $"{index}.txt"),
                "prefix-target",
                TestContext.Current.CancellationToken);
        }

        var tool = new FileSearchTool(_config, new NetclawPaths(), _openPathPolicy);
        var entryResult = await tool.ExecuteAsync(
            SearchInput(".", "absent", "name", 10, 2, 1),
            CreateContext(),
            TestContext.Current.CancellationToken);
        Assert.Contains("visited=2", entryResult, StringComparison.Ordinal);
        Assert.Contains("truncated=true", entryResult, StringComparison.Ordinal);

        var byteResult = await tool.ExecuteAsync(
            SearchInput(".", "target", "content", 10, 10, 4),
            CreateContext(),
            TestContext.Current.CancellationToken);
        Assert.Contains("matches=0", byteResult, StringComparison.Ordinal);
        Assert.Contains("content_bytes=4", byteResult, StringComparison.Ordinal);
        Assert.Contains("truncated=true", byteResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_search_skips_protected_descendants_and_rejects_invalid_bounds()
    {
        var denied = Path.Join(_project, "protected.txt");
        await File.WriteAllTextAsync(denied, "needle", TestContext.Current.CancellationToken);
        var context = CreateContext();
        var tool = new FileSearchTool(_config, new NetclawPaths(), new ToolPathPolicy([denied]));

        var result = await tool.ExecuteAsync(
            SearchInput(".", "needle", "content", 10, 10, 1024),
            context,
            TestContext.Current.CancellationToken);

        Assert.Contains("matches=0", result, StringComparison.Ordinal);
        Assert.DoesNotContain("protected.txt:", result, StringComparison.Ordinal);

        var invalidContext = CreateContext();
        var invalid = await new FileSearchTool(_config, new NetclawPaths(), _openPathPolicy).ExecuteAsync(
            ToolInput.Create(
                "Root", ".",
                "Query", "needle",
                "Mode", "content",
                "MaxResults", FileSearchTool.MaximumResults + 1),
            invalidContext,
            TestContext.Current.CancellationToken);
        Assert.Contains("MaxResults", invalid, StringComparison.Ordinal);
        Assert.Equal(ToolInvocationOutcomeCategory.InvalidInput, invalidContext.Receipt?.Category);
    }

    [Fact]
    public async Task File_search_propagates_caller_cancellation()
    {
        var context = CreateContext();
        var tool = new FileSearchTool(_config, new NetclawPaths(), _openPathPolicy);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.ExecuteAsync(
            ToolInput.Create("Root", ".", "Query", "x", "Mode", "content"),
            context,
            cancellation.Token));
        Assert.Null(context.Receipt);
    }

    [Fact]
    public async Task File_read_many_validates_denied_batch_before_returning_content()
    {
        var allowed = Path.Join(_project, "allowed.txt");
        var denied = Path.Join(_project, "denied.txt");
        await File.WriteAllTextAsync(allowed, "allowed-secret-marker", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(denied, "denied-secret-marker", TestContext.Current.CancellationToken);
        var context = CreateContext();
        var tool = new FileReadManyTool(_config, new NetclawPaths(), new ToolPathPolicy([denied]));

        var result = await tool.ExecuteAsync(
            ToolInput.Create("Paths", new[] { "allowed.txt", "denied.txt" }),
            context,
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain("allowed-secret-marker", result, StringComparison.Ordinal);
        Assert.DoesNotContain("denied-secret-marker", result, StringComparison.Ordinal);
        Assert.Equal(ToolInvocationOutcomeCategory.AccessDenied, context.Receipt?.Category);
        Assert.Empty(context.Receipt?.FileActivity ?? []);
    }

    [Fact]
    public async Task File_read_many_returns_bounded_labeled_sections_and_canonical_activity()
    {
        var first = Path.Join(_project, "a.txt");
        var second = Path.Join(_project, "b.txt");
        await File.WriteAllTextAsync(first, new string('a', 100), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(second, new string('b', 100), TestContext.Current.CancellationToken);
        var context = CreateContext();
        var tool = new FileReadManyTool(_config, new NetclawPaths(), _openPathPolicy);
        var totalLimit = first.Length + second.Length + 80;

        var result = await tool.ExecuteAsync(
            ToolInput.Create(
                "Paths", new[] { "a.txt", "b.txt" },
                "MaxCharsPerFile", 20,
                "MaxTotalChars", totalLimit),
            context,
            TestContext.Current.CancellationToken);

        Assert.True(result.Length <= totalLimit);
        Assert.Contains($"== {first} ==", result, StringComparison.Ordinal);
        Assert.Contains($"== {second} ==", result, StringComparison.Ordinal);
        Assert.Contains("[truncated]", result, StringComparison.Ordinal);
        Assert.Equal([first, second], context.Receipt!.FileActivity.Select(activity => activity.CanonicalPath));
        Assert.All(context.Receipt.FileActivity, activity => Assert.Equal(ToolFileActivityKind.Read, activity.Kind));
    }

    [Fact]
    public async Task File_read_many_rejects_duplicate_and_non_text_members_atomically()
    {
        var text = Path.Join(_project, "text.txt");
        var binary = Path.Join(_project, "binary.bin");
        await File.WriteAllTextAsync(text, "must-not-leak", TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(binary, [0xFF, 0xFE, 0xFD], TestContext.Current.CancellationToken);

        var duplicateContext = CreateContext();
        var tool = new FileReadManyTool(_config, new NetclawPaths(), _openPathPolicy);
        var duplicate = await tool.ExecuteAsync(
            ToolInput.Create("Paths", new[] { "text.txt", "./text.txt" }),
            duplicateContext,
            TestContext.Current.CancellationToken);
        Assert.Contains("duplicate", duplicate, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ToolInvocationOutcomeCategory.InvalidInput, duplicateContext.Receipt?.Category);

        var binaryContext = CreateContext();
        var binaryResult = await tool.ExecuteAsync(
            ToolInput.Create("Paths", new[] { "text.txt", "binary.bin" }),
            binaryContext,
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("must-not-leak", binaryResult, StringComparison.Ordinal);
        Assert.Equal(ToolInvocationOutcomeCategory.InvalidInput, binaryContext.Receipt?.Category);
        Assert.Empty(binaryContext.Receipt?.FileActivity ?? []);
    }

    [Fact]
    public async Task File_read_many_rejects_path_count_above_the_hard_ceiling()
    {
        var context = CreateContext();
        var tool = new FileReadManyTool(_config, new NetclawPaths(), _openPathPolicy);

        var result = await tool.ExecuteAsync(
            ToolInput.Create("Paths", Enumerable.Range(0, FileReadManyTool.MaximumPathCount + 1)
                .Select(index => $"{index}.txt")
                .ToArray()),
            context,
            TestContext.Current.CancellationToken);

        Assert.Contains($"between 1 and {FileReadManyTool.MaximumPathCount}", result, StringComparison.Ordinal);
        Assert.Equal(ToolInvocationOutcomeCategory.InvalidInput, context.Receipt?.Category);
    }

    [Fact]
    public async Task Json_read_projects_rfc6901_pointers_without_executable_queries()
    {
        var path = Path.Join(_project, "status.json");
        await File.WriteAllTextAsync(
            path,
            """{"status":"ready","items":[{"name":"first"}],"a/b":{"~key":7}}""",
            TestContext.Current.CancellationToken);
        var context = CreateContext();
        var tool = new JsonReadTool(_config, new NetclawPaths(), _openPathPolicy);

        var result = await tool.ExecuteAsync(
            ToolInput.Create(
                "Path", "status.json",
                "Pointers", new[] { "/status", "/items/0/name", "/a~1b/~0key" }),
            context,
            TestContext.Current.CancellationToken);

        using var projected = JsonDocument.Parse(result);
        Assert.Equal("ready", projected.RootElement.GetProperty("/status").GetString());
        Assert.Equal("first", projected.RootElement.GetProperty("/items/0/name").GetString());
        Assert.Equal(7, projected.RootElement.GetProperty("/a~1b/~0key").GetInt32());
        Assert.Equal(path, Assert.Single(context.Receipt!.FileActivity).CanonicalPath);
    }

    [Fact]
    public async Task Json_read_rejects_invalid_or_duplicate_pointers_before_projection()
    {
        var path = Path.Join(_project, "status.json");
        await File.WriteAllTextAsync(path, """{"status":"must-not-project"}""", TestContext.Current.CancellationToken);
        var tool = new JsonReadTool(_config, new NetclawPaths(), _openPathPolicy);

        var invalidContext = CreateContext();
        var invalid = await tool.ExecuteAsync(
            ToolInput.Create("Path", "status.json", "Pointers", new[] { "/status", "status" }),
            invalidContext,
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("must-not-project", invalid, StringComparison.Ordinal);
        Assert.Equal(ToolInvocationOutcomeCategory.InvalidInput, invalidContext.Receipt?.Category);

        var duplicateContext = CreateContext();
        var duplicate = await tool.ExecuteAsync(
            ToolInput.Create("Path", "status.json", "Pointers", new[] { "/status", "/status" }),
            duplicateContext,
            TestContext.Current.CancellationToken);
        Assert.Contains("duplicate", duplicate, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ToolInvocationOutcomeCategory.InvalidInput, duplicateContext.Receipt?.Category);
    }

    [Fact]
    public async Task Json_read_enforces_input_and_output_bounds_without_partial_values()
    {
        var path = Path.Join(_project, "large.json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(new { value = new string('x', 200) }),
            TestContext.Current.CancellationToken);
        var tool = new JsonReadTool(_config, new NetclawPaths(), _openPathPolicy);

        var inputContext = CreateContext();
        var input = await tool.ExecuteAsync(
            ToolInput.Create("Path", "large.json", "Pointers", new[] { "/value" }, "MaxInputBytes", 10),
            inputContext,
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain(new string('x', 20), input, StringComparison.Ordinal);
        Assert.Equal(ToolInvocationOutcomeCategory.InvalidInput, inputContext.Receipt?.Category);

        var outputContext = CreateContext();
        var output = await tool.ExecuteAsync(
            ToolInput.Create("Path", "large.json", "Pointers", new[] { "/value" }, "MaxOutputChars", 20),
            outputContext,
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain(new string('x', 20), output, StringComparison.Ordinal);
        Assert.Equal(ToolInvocationOutcomeCategory.InvalidInput, outputContext.Receipt?.Category);
    }

    [Fact]
    public async Task Json_read_rejects_malformed_json_and_pointer_count_above_the_ceiling()
    {
        var path = Path.Join(_project, "malformed.json");
        await File.WriteAllTextAsync(path, "{\"status\":", TestContext.Current.CancellationToken);
        var tool = new JsonReadTool(_config, new NetclawPaths(), _openPathPolicy);

        var malformedContext = CreateContext();
        var malformed = await tool.ExecuteAsync(
            ToolInput.Create("Path", "malformed.json", "Pointers", new[] { "/status" }),
            malformedContext,
            TestContext.Current.CancellationToken);
        Assert.Contains("Invalid JSON", malformed, StringComparison.Ordinal);
        Assert.Equal(ToolInvocationOutcomeCategory.InvalidInput, malformedContext.Receipt?.Category);

        var countContext = CreateContext();
        var count = await tool.ExecuteAsync(
            ToolInput.Create(
                "Path", "malformed.json",
                "Pointers", Enumerable.Range(0, JsonReadTool.MaximumPointerCount + 1)
                    .Select(index => $"/{index}")
                    .ToArray()),
            countContext,
            TestContext.Current.CancellationToken);
        Assert.Contains($"between 1 and {JsonReadTool.MaximumPointerCount}", count, StringComparison.Ordinal);
        Assert.Equal(ToolInvocationOutcomeCategory.InvalidInput, countContext.Receipt?.Category);
    }

    [Theory]
    [MemberData(nameof(ImageHeaders))]
    public void Image_dimension_reader_handles_supported_bounded_headers(
        string mimeType,
        byte[] header,
        int expectedWidth,
        int expectedHeight)
    {
        var status = ImageDimensionReader.Read(new MimeType(mimeType), header, out var dimensions);

        Assert.Equal(ImageDimensionStatus.Valid, status);
        Assert.Equal(new ImageDimensions(expectedWidth, expectedHeight), dimensions);
    }

    [Fact]
    public async Task File_read_returns_png_dimensions_and_rejects_malformed_supported_header()
    {
        var validPath = Path.Join(_project, "valid.png");
        var malformedPath = Path.Join(_project, "malformed.png");
        await File.WriteAllBytesAsync(validPath, PngHeader(640, 480), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(malformedPath, PngHeader(0, 480), TestContext.Current.CancellationToken);
        var tool = new FileReadTool(_config, new NetclawPaths(), _openPathPolicy);

        var validContext = CreateContext();
        var valid = await tool.ExecuteAsync(
            ToolInput.Create("Path", "valid.png"),
            validContext,
            TestContext.Current.CancellationToken);
        Assert.Contains("Type: image/png", valid, StringComparison.Ordinal);
        Assert.Contains("Dimensions: 640x480", valid, StringComparison.Ordinal);
        Assert.Equal(ToolInvocationOutcomeCategory.Success, validContext.Receipt?.Category);

        var malformedContext = CreateContext();
        var malformed = await tool.ExecuteAsync(
            ToolInput.Create("Path", "malformed.png"),
            malformedContext,
            TestContext.Current.CancellationToken);
        Assert.Contains("malformed", malformed, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ToolInvocationOutcomeCategory.InvalidInput, malformedContext.Receipt?.Category);
        Assert.Empty(malformedContext.Outputs.ModelInputFiles);
    }

    public static TheoryData<string, byte[], int, int> ImageHeaders => new()
    {
        { MimeTypeCatalog.ImagePng, PngHeader(640, 480), 640, 480 },
        { MimeTypeCatalog.ImageGif, GifHeader(320, 200), 320, 200 },
        { MimeTypeCatalog.ImageJpeg, JpegHeader(800, 600), 800, 600 },
        { MimeTypeCatalog.ImageWebp, WebpExtendedHeader(1024, 768), 1024, 768 }
    };

    private ToolExecutionContext CreateContext()
        => TestToolExecutionContext.CreateBound(
            "signalr/structured-workspace",
            _session,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                ProjectDirectory = _project
            });

    private static Dictionary<string, object?> SearchInput(
        string root,
        string query,
        string mode,
        int maxResults,
        int maxFiles,
        int maxContentBytes) =>
        new()
        {
            ["Root"] = root,
            ["Query"] = query,
            ["Mode"] = mode,
            ["MaxResults"] = maxResults,
            ["MaxFiles"] = maxFiles,
            ["MaxContentBytes"] = maxContentBytes
        };

    private static byte[] PngHeader(int width, int height)
    {
        var header = new byte[24];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(header, 0);
        new byte[] { 0, 0, 0, 13 }.CopyTo(header, 8);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(header, 12);
        WriteBigEndian(header, 16, width);
        WriteBigEndian(header, 20, height);
        return header;
    }

    private static byte[] GifHeader(int width, int height)
    {
        var header = new byte[10];
        Encoding.ASCII.GetBytes("GIF89a").CopyTo(header, 0);
        header[6] = (byte)width;
        header[7] = (byte)(width >> 8);
        header[8] = (byte)height;
        header[9] = (byte)(height >> 8);
        return header;
    }

    private static byte[] JpegHeader(int width, int height)
    {
        return
        [
            0xFF, 0xD8,
            0xFF, 0xC0, 0x00, 0x11, 0x08,
            (byte)(height >> 8), (byte)height,
            (byte)(width >> 8), (byte)width,
            0x03, 0x01, 0x11, 0x00, 0x02, 0x11, 0x00, 0x03, 0x11, 0x00
        ];
    }

    private static byte[] WebpExtendedHeader(int width, int height)
    {
        var header = new byte[30];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        Encoding.ASCII.GetBytes("WEBP").CopyTo(header, 8);
        Encoding.ASCII.GetBytes("VP8X").CopyTo(header, 12);
        WriteUInt24(header, 24, width - 1);
        WriteUInt24(header, 27, height - 1);
        return header;
    }

    private static void WriteBigEndian(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static void WriteUInt24(byte[] target, int offset, int value)
    {
        target[offset] = (byte)value;
        target[offset + 1] = (byte)(value >> 8);
        target[offset + 2] = (byte)(value >> 16);
    }
}
