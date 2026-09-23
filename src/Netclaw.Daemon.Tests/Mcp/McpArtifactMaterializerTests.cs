// -----------------------------------------------------------------------
// <copyright file="McpArtifactMaterializerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Daemon.Mcp;
using Netclaw.Media;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

/// <summary>
/// Verifies the security and output boundaries for MCP result artifacts.
/// </summary>
public sealed class McpArtifactMaterializerTests
{
    private static readonly byte[] ValidPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg==");

    [Fact]
    public async Task Valid_image_reaches_the_user_but_not_a_text_only_model()
    {
        // A text-only model cannot consume an image, but the user can still receive it.
        // This test proves that modality does not suppress a verified file output.
        // Arrange
        using var directory = new DisposableTempDir();
        var context = CreateContext(directory, ModelModality.Text);
        var materializer = CreateMaterializer(new MagicByteContentScanner(new ContentPolicy()));
        var artifacts = ProjectArtifacts(new DataContent(ValidPng, "image/png") { Name = "chart.png" });

        // Act
        var notes = await materializer.MaterializeAsync(
            artifacts,
            "smoke/chart",
            context.Invocation,
            TestContext.Current.CancellationToken);

        // Assert
        var output = Assert.Single(context.Outputs.FileAttachments);
        Assert.Empty(context.Outputs.ModelInputFiles);
        Assert.Equal("image/png", output.MimeType.Value);
        Assert.EndsWith(".png", output.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(output.FilePath));
        Assert.Contains(notes, note => note.Contains("no image modality", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Valid_image_reaches_an_image_capable_model_and_the_user()
    {
        // An image-capable model can consume the same verified file that the user receives.
        // This test proves that both outputs refer to one admitted artifact.
        // Arrange
        using var directory = new DisposableTempDir();
        var context = CreateContext(directory, ModelModality.Text | ModelModality.Image);
        var materializer = CreateMaterializer(new MagicByteContentScanner(new ContentPolicy()));
        var artifacts = ProjectArtifacts(new DataContent(ValidPng, "image/png") { Name = "chart.png" });

        // Act
        var notes = await materializer.MaterializeAsync(
            artifacts,
            "smoke/chart",
            context.Invocation,
            TestContext.Current.CancellationToken);

        // Assert
        var userOutput = Assert.Single(context.Outputs.FileAttachments);
        var modelOutput = Assert.Single(context.Outputs.ModelInputFiles);
        Assert.Equal(userOutput.FilePath, modelOutput.FilePath);
        Assert.Equal(userOutput.MimeType, modelOutput.MimeType);
        Assert.Empty(notes);
    }

    [Fact]
    public async Task Verified_non_model_image_reaches_only_the_user()
    {
        // The shared catalog accepts BMP files but does not permit them as model input.
        // This test proves that MCP output uses the same path-only file rule.
        // Arrange
        using var directory = new DisposableTempDir();
        var context = CreateContext(directory, ModelModality.Text | ModelModality.Image);
        var materializer = CreateMaterializer(new MagicByteContentScanner(new ContentPolicy()));
        byte[] validBmp = [0x42, 0x4D, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x28, 0, 0, 0];
        var artifacts = ProjectArtifacts(new DataContent(validBmp, "image/bmp") { Name = "chart.bmp" });

        // Act
        var notes = await materializer.MaterializeAsync(
            artifacts,
            "smoke/chart",
            context.Invocation,
            TestContext.Current.CancellationToken);

        // Assert
        var output = Assert.Single(context.Outputs.FileAttachments);
        Assert.Equal("image/bmp", output.MimeType.Value);
        Assert.Empty(context.Outputs.ModelInputFiles);
        Assert.Contains(notes, note => note.Contains("format not inlineable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invalid_magic_bytes_produce_no_output_file()
    {
        // The server MIME label is untrusted until the shared scanner verifies the bytes.
        // This test proves that a false PNG claim cannot create any output.
        // Arrange
        using var directory = new DisposableTempDir();
        var context = CreateContext(directory, ModelModality.Image);
        var materializer = CreateMaterializer(new MagicByteContentScanner(new ContentPolicy()));
        var artifacts = ProjectArtifacts(new DataContent(new byte[] { 1, 2, 3 }, "image/png"));

        // Act
        var notes = await materializer.MaterializeAsync(
            artifacts,
            "smoke/chart",
            context.Invocation,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(context.Outputs.FileAttachments);
        Assert.Empty(context.Outputs.ModelInputFiles);
        Assert.False(Directory.Exists(context.SessionStorage!.ArtifactDirectory.Value));
        Assert.Contains("content validation failed", Assert.Single(notes));
    }

    [Fact]
    public async Task Scanner_runs_before_the_artifact_directory_is_created()
    {
        // Unverified bytes must not reach session storage, even for a short interval.
        // This test proves that content admission occurs before the first file write.
        // Arrange
        using var directory = new DisposableTempDir();
        var context = CreateContext(directory, ModelModality.Image);
        var artifactDirectory = context.SessionStorage!.ArtifactDirectory.Value;
        var scanner = new DelegatingScanner((_, _, _, _) =>
        {
            Assert.False(Directory.Exists(artifactDirectory));
            return Task.FromResult(ContentScanResult.Allowed(new VerifiedMimeType(MimeTypeCatalog.ImagePng)));
        });
        var materializer = CreateMaterializer(scanner);
        var artifacts = CreateArtifacts((new byte[] { 1 }, "image/png", "chart.png"));

        // Act
        await materializer.MaterializeAsync(
            artifacts,
            "smoke/chart",
            context.Invocation,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(context.Outputs.FileAttachments);
        Assert.True(Directory.Exists(artifactDirectory));
    }

    [Fact]
    public async Task Verified_MIME_controls_the_extension_and_output_type()
    {
        // The server can provide a traversal name and a false MIME label.
        // This test proves that the scanner result controls the safe output identity.
        // Arrange
        using var directory = new DisposableTempDir();
        var context = CreateContext(directory, ModelModality.Image);
        var scanner = new DelegatingScanner((_, _, _, _) => Task.FromResult(
            ContentScanResult.Allowed(new VerifiedMimeType(MimeTypeCatalog.ImageJpeg))));
        var materializer = CreateMaterializer(scanner);
        var artifacts = CreateArtifacts((new byte[] { 1 }, "image/png", "../../report.exe"));

        // Act
        await materializer.MaterializeAsync(
            artifacts,
            "smoke/chart",
            context.Invocation,
            TestContext.Current.CancellationToken);

        // Assert
        var output = Assert.Single(context.Outputs.FileAttachments);
        Assert.Equal("image/jpeg", output.MimeType.Value);
        Assert.Equal("report.jpg", output.FileName);
        Assert.EndsWith(".jpg", output.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            Path.GetFullPath(context.SessionStorage!.ArtifactDirectory.Value),
            Path.GetDirectoryName(Path.GetFullPath(output.FilePath)));
    }

    [Fact]
    public async Task Supported_filename_extension_resolves_a_generic_declared_MIME()
    {
        // MCP servers can omit a specific MIME type while they provide a useful name.
        // This test proves that the shared scanner applies its existing extension rule.
        // Arrange
        using var directory = new DisposableTempDir();
        var context = CreateContext(directory, ModelModality.Image);
        var materializer = CreateMaterializer(new MagicByteContentScanner(new ContentPolicy()));
        var artifacts = CreateArtifacts((ValidPng, "application/octet-stream", "chart.png"));

        // Act
        await materializer.MaterializeAsync(
            artifacts,
            "smoke/chart",
            context.Invocation,
            TestContext.Current.CancellationToken);

        // Assert
        var output = Assert.Single(context.Outputs.FileAttachments);
        Assert.Equal("image/png", output.MimeType.Value);
        Assert.Equal("chart.png", output.FileName);
    }

    [Fact]
    public async Task Cancellation_removes_files_and_registers_no_outputs()
    {
        // Cancellation can occur after one candidate reaches storage and before the next scan.
        // This test proves that the call leaves no partial file or registered output.
        // Arrange
        using var directory = new DisposableTempDir();
        var context = CreateContext(directory, ModelModality.Image);
        var scannerCallCount = 0;
        var scanner = new DelegatingScanner((_, _, _, _) =>
        {
            if (scannerCallCount++ == 0)
                return Task.FromResult(ContentScanResult.Allowed(new VerifiedMimeType(MimeTypeCatalog.ImagePng)));
            throw new OperationCanceledException();
        });
        var materializer = CreateMaterializer(scanner);
        var artifacts = CreateArtifacts(
            (new byte[] { 1 }, "image/png", "one.png"),
            (new byte[] { 2 }, "image/png", "two.png"));

        // Act
        await Assert.ThrowsAsync<OperationCanceledException>(() => materializer.MaterializeAsync(
            artifacts,
            "smoke/chart",
            context.Invocation,
            TestContext.Current.CancellationToken));

        // Assert
        Assert.Empty(context.Outputs.FileAttachments);
        Assert.Empty(context.Outputs.ModelInputFiles);
        Assert.Empty(Directory.GetFiles(context.SessionStorage!.ArtifactDirectory.Value));
    }

    [Fact]
    public async Task Missing_session_storage_produces_a_note_without_a_scan()
    {
        // A sessionless tool call has no owned artifact directory.
        // This test proves that Netclaw does not invent another storage path.
        // Arrange
        var context = TestToolExecutionContext.CreateUnbound(new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            ModelInputModalities = ModelModality.Image,
        });
        var scanner = new DelegatingScanner((_, _, _, _) => Task.FromResult(
            ContentScanResult.Allowed(new VerifiedMimeType(MimeTypeCatalog.ImagePng))));
        var materializer = CreateMaterializer(scanner);
        var artifacts = CreateArtifacts((new byte[] { 1 }, "image/png", "chart.png"));

        // Act
        var notes = await materializer.MaterializeAsync(
            artifacts,
            "smoke/chart",
            context.Invocation,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, scanner.CallCount);
        Assert.Empty(context.Outputs.FileAttachments);
        Assert.Contains("no session storage", Assert.Single(notes));
    }

    [Fact]
    public async Task Allowed_scan_without_verified_MIME_produces_no_output()
    {
        // A scanner result without a verified MIME type cannot authorize a file identity.
        // This test proves that an incomplete allow result fails closed.
        // Arrange
        using var directory = new DisposableTempDir();
        var context = CreateContext(directory, ModelModality.Image);
        var scanner = new DelegatingScanner((_, _, _, _) => Task.FromResult(new ContentScanResult(true)));
        var materializer = CreateMaterializer(scanner);
        var artifacts = CreateArtifacts((new byte[] { 1 }, "image/png", "chart.png"));

        // Act
        var notes = await materializer.MaterializeAsync(
            artifacts,
            "smoke/chart",
            context.Invocation,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(context.Outputs.FileAttachments);
        Assert.False(Directory.Exists(context.SessionStorage!.ArtifactDirectory.Value));
        Assert.Contains("content validation failed", Assert.Single(notes));
    }

    private static McpArtifactMaterializer CreateMaterializer(IContentScanner scanner)
        => new(scanner, NullLogger<McpArtifactMaterializer>.Instance);

    private static ToolExecutionContext CreateContext(DisposableTempDir directory, ModelModality modalities)
    {
        var storage = SessionStoragePaths.CreateLegacy(
            Path.Combine(directory.Path, "session"),
            Path.Combine(directory.Path, "logs"),
            "test-thread");
        return TestToolExecutionContext.CreateBoundWithStorage(
            "test/thread",
            storage,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                ModelInputModalities = modalities,
            });
    }

    private static IReadOnlyList<McpResultArtifact> ProjectArtifacts(params DataContent[] contents)
        => McpToolResultFormatter.Project(contents, "smoke/chart").Artifacts;

    private static IReadOnlyList<McpResultArtifact> CreateArtifacts(
        params (byte[] Bytes, string MimeType, string Name)[] artifacts)
        => artifacts.Select(static artifact => new McpResultArtifact(
                artifact.Bytes,
                new DeclaredMimeType(artifact.MimeType),
                artifact.Name))
            .ToArray();

    private sealed class DelegatingScanner(
        Func<ReadOnlyMemory<byte>, string, string, CancellationToken, Task<ContentScanResult>> scan)
        : IContentScanner
    {
        private readonly Func<ReadOnlyMemory<byte>, string, string, CancellationToken, Task<ContentScanResult>> _scan = scan;

        internal int CallCount { get; private set; }

        public Task<ContentScanResult> ScanAsync(
            ReadOnlyMemory<byte> content,
            string filename,
            string declaredMimeType,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return _scan(content, filename, declaredMimeType, cancellationToken);
        }
    }
}
