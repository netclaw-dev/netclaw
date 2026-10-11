// -----------------------------------------------------------------------
// <copyright file="McpArtifactAdmissionMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Daemon.Mcp;
using Netclaw.Media;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.MutationTests;

public sealed class McpArtifactAdmissionMutationTests
{
    [Fact]
    public void Scanner_rejection_fails_admission_with_a_verified_MIME()
    {
        // A scanner can reject bytes after it detects their MIME type.
        // This test proves that detection cannot override an explicit rejection.
        // Arrange
        var scanResult = new ContentScanResult(
            false,
            ContentScanError.AntivirusDetection,
            new MimeType(MimeTypeCatalog.ImagePng),
            VerifiedMimeType: new VerifiedMimeType(MimeTypeCatalog.ImagePng));

        // Act
        var admitted = McpArtifactMaterializer.TryAdmit(scanResult, out _, out _);

        // Assert
        Assert.False(admitted);
    }

    [Fact]
    public void Scanner_allow_without_a_verified_MIME_fails_admission()
    {
        // An allow flag cannot select a safe extension or output MIME by itself.
        // This test proves that missing verification fails closed.
        // Arrange
        var scanResult = new ContentScanResult(true);

        // Act
        var admitted = McpArtifactMaterializer.TryAdmit(scanResult, out _, out _);

        // Assert
        Assert.False(admitted);
    }
}
