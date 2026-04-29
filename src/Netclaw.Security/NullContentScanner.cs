// -----------------------------------------------------------------------
// <copyright file="NullContentScanner.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Security;

/// <summary>
/// No-op content scanner that allows all content through.
/// Used as the default until a real scanner (e.g., ClamAV) is configured.
/// </summary>
public sealed class NullContentScanner : IContentScanner
{
    public Task<ContentScanResult> ScanAsync(
        ReadOnlyMemory<byte> content,
        string filename,
        string declaredMimeType,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ContentScanResult.Allowed());
    }
}
