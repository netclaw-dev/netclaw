// -----------------------------------------------------------------------
// <copyright file="McpArtifactAdmissionMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Daemon.Mcp;
using Netclaw.Media;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.MutationTests;

public sealed class McpArtifactAdmissionMutationTests : IDisposable
{
    private readonly string _basePath = Path.Combine(
        Path.GetTempPath(),
        "netclaw-mutation-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Scanner_rejection_blocks_a_candidate_that_has_a_verified_MIME()
    {
        // A scanner can reject bytes after it detects their MIME type.
        // This test proves that detection cannot override an explicit rejection.
        // Arrange
        var scanResult = new ContentScanResult(
            false,
            ContentScanError.AntivirusDetection,
            new MimeType(MimeTypeCatalog.ImagePng),
            VerifiedMimeType: new VerifiedMimeType(MimeTypeCatalog.ImagePng));
        var materializer = CreateMaterializer(scanResult);
        var context = CreateContext();
        var artifacts = CreateArtifacts();

        // Act
        var notes = await materializer.MaterializeAsync(
            artifacts,
            "test/image",
            context,
            CancellationToken.None);

        // Assert
        Assert.Empty(context.Outputs.FileAttachments);
        Assert.False(Directory.Exists(context.SessionStorage!.ArtifactDirectory.Value));
        Assert.Contains(notes, note => note.Contains("content validation failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Scanner_allow_without_a_verified_MIME_blocks_the_candidate()
    {
        // An allow flag cannot select a safe extension or output MIME by itself.
        // This test proves that missing verification fails closed.
        // Arrange
        var materializer = CreateMaterializer(new ContentScanResult(true));
        var context = CreateContext();
        var artifacts = CreateArtifacts();

        // Act
        var notes = await materializer.MaterializeAsync(
            artifacts,
            "test/image",
            context,
            CancellationToken.None);

        // Assert
        Assert.Empty(context.Outputs.FileAttachments);
        Assert.False(Directory.Exists(context.SessionStorage!.ArtifactDirectory.Value));
        Assert.Contains(notes, note => note.Contains("content validation failed", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_basePath))
            Directory.Delete(_basePath, recursive: true);
    }

    private McpArtifactMaterializer CreateMaterializer(ContentScanResult scanResult)
        => new(new FixedScanner(scanResult), NullLogger<McpArtifactMaterializer>.Instance);

    private ToolInvocationContext CreateContext()
    {
        var storage = SessionStoragePaths.CreateLegacy(
            Path.Combine(_basePath, "session"),
            Path.Combine(_basePath, "logs"),
            "test-session");
        return new ToolInvocationContext(
            new ToolRunScope
            {
                Session = new ToolSessionScope.Bound("test/session", storage),
                Audience = TrustAudience.Personal,
                InlineOutputBudget = InlineOutputBudget.Default,
                InteractiveApproval = new InteractiveApprovalCapability.Unavailable(),
                ModelInputModalities = ModelModality.Image,
            },
            ToolExecutionTimeout.Default);
    }

    private static IReadOnlyList<McpResultArtifact> CreateArtifacts()
        =>
        [
            new McpResultArtifact(
                new byte[] { 1, 2, 3 },
                new DeclaredMimeType(MimeTypeCatalog.ImagePng),
                "image.png"),
        ];

    private sealed class FixedScanner(ContentScanResult result) : IContentScanner
    {
        public Task<ContentScanResult> ScanAsync(
            ReadOnlyMemory<byte> content,
            string filename,
            string declaredMimeType,
            CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }
}
